using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DirectShowLib;
using TapeRescue.Interop;
using TapeRescue.Models;

namespace TapeRescue.Services;

public enum ConnectionState { Searching, Connected, NoCamcorder, NoFirewire, FirewireDriverProblem, DeviceBusy, Error }

public enum TransportMode { Unknown, Stopped, Playing, Paused, FastForward, Rewind, Recording }

public enum TransportCommand { Play, Pause, Stop, FastForward, Rewind }

public sealed record ConnectionInfo(
    ConnectionState State,
    string? DeviceName = null,
    string? Message = null,
    string? TechnicalDetail = null,
    bool PreviewAvailable = false,
    bool CaptureAvailable = false);

public sealed record TransportSnapshot(TransportMode Mode, int RawMode, TapeTime? Timecode, TapeTime? Counter, bool? TapePresent);

public sealed record CommandResult(bool Ok, string? Message = null);

/// <param name="Blank">How long the picture has been blank (unrecorded tape); the capture stops at <see cref="TransportController.BlankStopAfter"/>.</param>
/// <param name="LiveMp4Dropped">Frames the live MP4 couldn't keep up with (-1 when no live MP4 is running).</param>
public sealed record CaptureProgress(TimeSpan Elapsed, long Bytes, int DroppedFrames, TimeSpan Blank = default, long LiveMp4Dropped = -1);

/// <param name="LiveMp4">The MP4 made during the capture, still finishing; null when that wasn't on or possible.</param>
/// <param name="LiveMp4Unavailable">The live MP4 was asked for but couldn't run (the preview isn't using FFmpeg).</param>
public sealed record CaptureOutcome(
    bool Ok,
    string? Path = null,
    string? Message = null,
    TimeSpan Elapsed = default,
    long Bytes = 0,
    int DroppedFrames = 0,
    LiveMp4Encoder? LiveMp4 = null,
    bool LiveMp4Unavailable = false);

/// <summary>
/// Owns the DirectShow graph for the "Microsoft DV Camera and VCR" device.
///
///   Camcorder [DV A/V Out] → Smart Tee ─ Preview → DV Splitter → DV Decoder → Sample Grabber → Null Renderer
///                                     │                        └ audio → speakers
///                                     └ Capture → DV Splitter → AVI Mux → File Writer   (only while capturing)
///
/// The capture branch stores the camcorder's DV untouched (type-2 AVI: DV video + PCM audio), so nothing is re-encoded.
/// Everything runs on a private STA thread; events are raised on that thread.
/// </summary>
public sealed class TransportController : IDisposable
{
    private const int PollIntervalMs = 200;
    private const int RescanIntervalMs = 3000;
    private const int MaxPollFailures = 5;

    // HRESULTs that mean "someone else has the device". 0x80070004 is what MSDV returns when
    // another program (e.g. WinDV) is already streaming from the camcorder.
    private const int ERROR_TOO_MANY_OPEN_FILES = unchecked((int)0x80070004);
    private const int ERROR_BUSY = unchecked((int)0x800700AA);
    private const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);
    private const int ERROR_NO_SYSTEM_RESOURCES = unchecked((int)0x800705AA);
    private const int ERROR_NOT_READY = unchecked((int)0x80070015);
    private const int ERROR_SEM_TIMEOUT = unchecked((int)0x80070079);
    private const int ERROR_TIMEOUT = unchecked((int)0x800705B4);
    private const int VFW_E_CANNOT_CONNECT = unchecked((int)0x80040217);

    private readonly DeviceThread _thread = new("Tape Time Machine DV device");
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _rescanTimer;

    private IFilterGraph2? _graph;
    private ICaptureGraphBuilder2? _builder;
    private IBaseFilter? _source;
    private IBaseFilter? _smartTee;
    private IBaseFilter? _previewSplitter;
    private IBaseFilter? _decoder;
    private IBaseFilter? _grabber;
    private FfmpegPreview? _ffmpegPreview;
    private LiveMp4Encoder? _live;
    private IBaseFilter? _audioGrabber;
    private IBaseFilter? _nullRenderer;
    private IAMExtTransport? _transport;
    private IAMTimecodeReader? _tcReader;
    private IMediaControl? _control;
    private IMediaEventEx? _events;
    private bool _teeConnected;

    // Capture branch (only while capturing)
    private IBaseFilter? _captureSplitter;
    private IBaseFilter? _mux;
    private IBaseFilter? _fileWriter;
    private IAMDroppedFrames? _dropped;
    private int _droppedBaseline;
    private string? _capturePath;
    private readonly Stopwatch _captureClock = new();
    private int _notPlayingPolls;
    private bool _captureSawPlaying;
    private long _captureStartTicks, _captureGrewTicks, _captureBytes;

    /// <summary>A capture stops by itself after this long with no picture: the rest of the tape is blank.</summary>
    public static readonly TimeSpan BlankStopAfter = TimeSpan.FromMinutes(2);

    private readonly Reading _timecode = new("timecode", DvConst.ED_DEVCAP_TIMECODE_READ);
    private readonly Reading _counter = new("counter", DvConst.ED_DEVCAP_RTC_READ);
    private int _pollFailures, _pollCount;
    private bool? _tapePresent;
    private TransportMode _mode = TransportMode.Unknown;
    private ExtTransportModes _lastRawMode = (ExtTransportModes)(-1);
    private string? _deviceName;
    private ConnectionInfo? _lastInfo;
    private bool _disposed;

    /// <summary>Preview frames for the UI.</summary>
    public FrameSink Frames { get; } = new();

    /// <summary>Preview audio level and format for the UI's meter.</summary>
    public AudioMeter Audio { get; } = new();

    public event Action<ConnectionInfo>? ConnectionChanged;
    public event Action<TransportSnapshot>? SnapshotUpdated;
    public event Action<CaptureProgress>? CaptureProgressed;
    /// <summary>A capture that ended on its own (tape stopped, disk nearly full, camcorder unplugged).</summary>
    public event Action<CaptureOutcome>? CaptureEndedAutomatically;

    public Task ConnectAsync() => _thread.InvokeAsync(() => Connect(quiet: false));

    public Task<CommandResult> SendAsync(TransportCommand command) => _thread.InvokeAsync(() => Send(command));

#if DEBUG
    /// <summary>Debug-only preview experiments: "noclock", "nosink" or "half".</summary>
    public static string? DebugPreviewVariant { get; set; }

    private void ApplyDebugPreviewVariant()
    {
        switch (DebugPreviewVariant)
        {
            case "noclock":
                // No reference clock: renderers never consider a frame late, so no quality-control messages.
                int hr = ((IMediaFilter)_graph!).SetSyncSource(null);
                Log.Info($"Preview variant noclock: SetSyncSource(null) {Hex(hr)}");
                break;
            case "nosink":
                // Swallow quality messages at the grabber so they never reach the DV decoder.
                IPin? grabberIn = DsFindPin.ByDirection(_grabber!, PinDirection.Input, 0);
                if (grabberIn is IQualityControl qc)
                {
                    int r = qc.SetSink(new QualitySwallow());
                    Log.Info($"Preview variant nosink: SetSink {Hex(r)}");
                }
                else Log.Warn("Preview variant nosink: grabber input pin has no IQualityControl");
                if (grabberIn is not null) Marshal.ReleaseComObject(grabberIn);
                break;
            case "half":
                Log.Info("Preview variant half: DV decoder at 360x240");
                break;
        }
    }

    [ComVisible(true)]
    private sealed class QualitySwallow : IQualityControl
    {
        public int Notify(IBaseFilter pSelf, Quality q) => 0;
        public int SetSink(IQualityControl piqc) => 0;
    }

    /// <summary>Debug-only: send any ED_MODE_* value (for diagnosing camcorders that ignore some commands).</summary>
    public Task<int> SendRawModeAsync(int mode) => _thread.InvokeAsync(() =>
    {
        if (_transport is null) return unchecked((int)0x80004005);
        int hr = _transport.put_Mode((ExtTransportModes)mode);
        Log.Info($"Raw mode {mode} -> {Hex(hr)}");
        Poll();
        return hr;
    });
#endif

    /// <param name="liveMp4">Also make the Jellyfin MP4 while the tape plays (needs the FFmpeg preview).</param>
    public Task<CaptureOutcome> StartCaptureAsync(string path, bool liveMp4 = false) => _thread.InvokeAsync(() => StartCapture(path, liveMp4));

    public Task<CaptureOutcome> StopCaptureAsync() => _thread.InvokeAsync(() => StopCapture(restartPreview: true));

    // ---------------------------------------------------------------- connection

    private void Connect(bool quiet)
    {
        if (_disposed) return;
        StopTimers();
        Teardown();
        if (!quiet) Raise(new ConnectionInfo(ConnectionState.Searching));

        DsDevice[] devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        try
        {
            DsDevice? dv = devices.FirstOrDefault(IsDvDevice);
            if (dv is null)
            {
                var fw = FirewireDiagnostics.CheckController();
                Log.Info($"No DV device. FireWire controller: {fw.State} '{fw.ControllerName}' code {fw.ErrorCode}");
                Raise(fw.State switch
                {
                    FirewireState.NoController => new ConnectionInfo(ConnectionState.NoFirewire),
                    FirewireState.DriverProblem => new ConnectionInfo(ConnectionState.FirewireDriverProblem,
                        TechnicalDetail: $"{fw.ControllerName}, Device Manager code {fw.ErrorCode}"),
                    _ => new ConnectionInfo(ConnectionState.NoCamcorder),
                });
                StartRescan();
                return;
            }

            _deviceName = FirewireDiagnostics.GetCamcorderName() ?? dv.Name;
            Log.Info($"Opening '{_deviceName}' ({dv.DevicePath})");
            bool preview = BuildGraph(dv);

            _timecode.Reset();
            _counter.Reset();
            _pollFailures = 0;
            _pollCount = 0;
            _tapePresent = null;
            _lastRawMode = (ExtTransportModes)(-1);

            Raise(new ConnectionInfo(ConnectionState.Connected, _deviceName,
                PreviewAvailable: preview, CaptureAvailable: _teeConnected));
            StartPolling();
            Poll();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Log.Error("Opening the camcorder failed", ex);
            Teardown();
            // No automatic retry here: repeatedly opening the device while another program is
            // capturing sends it extra traffic. The user selects "Check again" when ready.
            Raise(IsBusy(ex.HResult)
                ? new ConnectionInfo(ConnectionState.DeviceBusy, _deviceName, TechnicalDetail: Hex(ex.HResult))
                : new ConnectionInfo(ConnectionState.Error, _deviceName, ex.Message, Hex(ex.HResult)));
        }
        finally
        {
            foreach (var d in devices) d.Dispose();
        }
    }

    private static bool IsDvDevice(DsDevice d) =>
        (d.DevicePath?.Contains("avc#", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (d.Name?.Contains("DV Camera and VCR", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Builds the preview half of the graph. Returns false if only transport control works.</summary>
    private bool BuildGraph(DsDevice dv)
    {
        _graph = (IFilterGraph2)new FilterGraph();
        _builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
        Check(_builder.SetFiltergraph(_graph));
        Check(_graph.AddSourceFilterForMoniker(dv.Mon, null, "DV Camcorder", out _source));

        _transport = _source as IAMExtTransport
            ?? throw new InvalidOperationException("This device doesn't accept tape transport commands.");
        _tcReader = _source as IAMTimecodeReader;
        _control = (IMediaControl)_graph;
        _events = (IMediaEventEx)_graph;
        Log.Info($"Transport control available. Timecode reader: {(_tcReader is null ? "no" : "yes")}");

        // Smart Tee: the capture branch always has priority and preview frames are dropped instead if the
        // preview falls behind. (An Infinite Pin Tee was tried; it made the capture drop frames.)
        _smartTee = (IBaseFilter)new SmartTee();
#if DEBUG
        if (DebugPreviewVariant == "inftee")
        {
            Marshal.ReleaseComObject(_smartTee);
            _smartTee = (IBaseFilter)new InfTee();
            Log.Info("Preview variant inftee: Infinite Pin Tee instead of Smart Tee");
        }
#endif
        Check(_graph.AddFilter(_smartTee, "Smart Tee"));

        // Connect the camcorder's A/V pin ourselves: when another program is already capturing,
        // this is where Windows says so (0x80070004), while RenderStream only reports E_INVALIDARG.
        int hr = ConnectAvOutTo(_smartTee);
        if (hr < 0)
        {
            if (IsBusy(hr)) throw new COMException("Another program is capturing from the camcorder.", hr);
            Log.Warn($"Camcorder video unavailable {Hex(hr)}; transport control only");
            return false;
        }
        _teeConnected = true;

        try
        {
            BuildPreviewBranch();
        }
        catch (COMException ex)
        {
            Log.Error("Preview branch failed; capture still possible", ex);
            return false;
        }
#if DEBUG
        ApplyDebugPreviewVariant();
#endif
        ApplyPreviewVolume();

        hr = _control.Run();
        if (hr < 0)
        {
            if (IsBusy(hr)) throw new COMException("Another program is capturing from the camcorder.", hr);
            Log.Warn($"Preview would not start {Hex(hr)}");
            return false;
        }
        return true;
    }

    private void BuildPreviewBranch()
    {
        string? ffmpeg = PostProcess.FindFfmpeg();
#if DEBUG
        if (DebugPreviewVariant == "msdecoder") ffmpeg = null;
#endif
        if (ffmpeg is not null)
        {
            BuildFfmpegPreviewBranch(ffmpeg);
            return;
        }
        Log.Warn("FFmpeg not found; using the Windows DV decoder for preview (may show blocks on motion)");

        _previewSplitter = (IBaseFilter)new DVSplitter();
        Check(_graph!.AddFilter(_previewSplitter, "DV Splitter (preview)"));
        ConnectPins(TeePin("Preview", 1), DsFindPin.ByDirection(_previewSplitter, PinDirection.Input, 0), direct: true);

        _decoder = (IBaseFilter)new DVVideoCodec();
        if (_decoder is IIPDVDec ipdv)
        {
            var resolution = DVDecoderResolution.r720x480;
#if DEBUG
            if (DebugPreviewVariant == "half") resolution = DVDecoderResolution.r360x240;
#endif
            ipdv.put_IPDisplay(resolution);
        }
        Check(_graph.AddFilter(_decoder, "DV Video Decoder"));

        _grabber = (IBaseFilter)new SampleGrabber();
        var grabber = (ISampleGrabber)_grabber;
        var wanted = new AMMediaType { majorType = MediaType.Video, subType = MediaSubType.RGB32, formatType = FormatType.VideoInfo };
        Check(grabber.SetMediaType(wanted));
        DsUtils.FreeAMMediaType(wanted);
        Check(grabber.SetBufferSamples(false));
        Check(grabber.SetOneShot(false));
        Check(grabber.SetCallback(Frames, 1));
        Check(_graph.AddFilter(_grabber, "Preview Grabber"));

        _nullRenderer = (IBaseFilter)new NullRenderer();
        Check(_graph.AddFilter(_nullRenderer, "Null Renderer"));

        ConnectPins(DsFindPin.ByDirection(_previewSplitter, PinDirection.Output, 0), DsFindPin.ByDirection(_decoder, PinDirection.Input, 0));
        ConnectPins(DsFindPin.ByDirection(_decoder, PinDirection.Output, 0), DsFindPin.ByDirection(_grabber, PinDirection.Input, 0));
        ConnectPins(DsFindPin.ByDirection(_grabber, PinDirection.Output, 0), DsFindPin.ByDirection(_nullRenderer, PinDirection.Input, 0));

        var connected = new AMMediaType();
        Check(grabber.GetConnectedMediaType(connected));
        try
        {
            if (connected.formatType == FormatType.VideoInfo && connected.formatPtr != IntPtr.Zero)
            {
                var vih = (VideoInfoHeader)Marshal.PtrToStructure(connected.formatPtr, typeof(VideoInfoHeader))!;
                int w = vih.BmiHeader.Width, h = vih.BmiHeader.Height;
                Frames.Configure(w, Math.Abs(h), bottomUp: h > 0);
                Log.Info($"Preview frames {w}x{h} RGB32");
            }
            else throw new COMException("The preview format isn't RGB32 video.", VFW_E_CANNOT_CONNECT);
        }
        finally { DsUtils.FreeAMMediaType(connected); }

        // Let them hear the tape too (no speakers is fine), with a level meter tapped on the way.
        IPin? audio = DsFindPin.ByDirection(_previewSplitter, PinDirection.Output, 1);
        if (audio is not null)
        {
            try
            {
                if (!TryRenderAudioThroughMeter(audio))
                {
                    int hr = _graph.Render(audio);
                    Log.Info($"Preview audio (no meter) {Hex(hr)}");
                }
            }
            finally { Marshal.ReleaseComObject(audio); }
        }
    }

    /// <summary>
    /// Preview branch that never decodes from the driver's buffers:
    ///   Smart Tee preview → Sample Grabber (copies each raw DV frame to <see cref="FfmpegPreview"/>) → DV Splitter
    ///   → video to a Null Renderer, audio → level meter → speakers.
    /// </summary>
    private void BuildFfmpegPreviewBranch(string ffmpeg)
    {
        _ffmpegPreview = new FfmpegPreview(Frames, ffmpeg);

        _grabber = (IBaseFilter)new SampleGrabber();
        var grabber = (ISampleGrabber)_grabber;
        var wanted = new AMMediaType { majorType = MediaType.Interleaved };
        Check(grabber.SetMediaType(wanted));
        DsUtils.FreeAMMediaType(wanted);
        Check(grabber.SetBufferSamples(false));
        Check(grabber.SetOneShot(false));
        Check(grabber.SetCallback(_ffmpegPreview, 1));
        Check(_graph!.AddFilter(_grabber, "DV Copy for Preview"));
        ConnectPins(TeePin("Preview", 1), DsFindPin.ByDirection(_grabber, PinDirection.Input, 0), direct: true);

        _previewSplitter = (IBaseFilter)new DVSplitter();
        Check(_graph.AddFilter(_previewSplitter, "DV Splitter (preview)"));
        ConnectPins(DsFindPin.ByDirection(_grabber, PinDirection.Output, 0), DsFindPin.ByDirection(_previewSplitter, PinDirection.Input, 0), direct: true);

        _nullRenderer = (IBaseFilter)new NullRenderer();
        Check(_graph.AddFilter(_nullRenderer, "Null Renderer"));
        ConnectPins(DsFindPin.ByDirection(_previewSplitter, PinDirection.Output, 0), DsFindPin.ByDirection(_nullRenderer, PinDirection.Input, 0));
        Log.Info("Preview decoded by FFmpeg from copied DV frames");

        IPin? audio = DsFindPin.ByDirection(_previewSplitter, PinDirection.Output, 1);
        if (audio is not null)
        {
            try
            {
                if (!TryRenderAudioThroughMeter(audio))
                {
                    int hr = _graph.Render(audio);
                    Log.Info($"Preview audio (no meter) {Hex(hr)}");
                }
            }
            finally { Marshal.ReleaseComObject(audio); }
        }
    }

    private volatile bool _previewMuted;

    /// <summary>
    /// Silences (or restores) the tape's sound on the speakers. Only the preview's audio renderer changes: the
    /// capture branch records the sound regardless, and the level meter sits before the renderer so it keeps moving.
    /// </summary>
    public Task SetPreviewMutedAsync(bool muted) => _thread.InvokeAsync(() =>
    {
        _previewMuted = muted;
        ApplyPreviewVolume();
    });

    private void ApplyPreviewVolume()
    {
        if (_graph is not IBasicAudio audio) return;
        try
        {
            int hr = audio.put_Volume(_previewMuted ? -10000 : 0); // hundredths of a dB; -10000 is silent
            Log.Info($"Preview sound {(_previewMuted ? "muted" : "on")} {Hex(hr)}");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { Log.Warn($"Preview volume: {ex.Message}"); }
    }

    /// <summary>Splitter audio → Sample Grabber (PCM, feeds <see cref="Audio"/>) → speakers.</summary>
    private bool TryRenderAudioThroughMeter(IPin audioOut)
    {
        try
        {
            _audioGrabber = (IBaseFilter)new SampleGrabber();
            var grabber = (ISampleGrabber)_audioGrabber;
            var wanted = new AMMediaType { majorType = MediaType.Audio, subType = MediaSubType.PCM, formatType = FormatType.WaveEx };
            Check(grabber.SetMediaType(wanted));
            DsUtils.FreeAMMediaType(wanted);
            Check(grabber.SetBufferSamples(false));
            Check(grabber.SetOneShot(false));
            Check(grabber.SetCallback(Audio, 1));
            Check(_graph!.AddFilter(_audioGrabber, "Audio Meter"));

            IPin grabberIn = DsFindPin.ByDirection(_audioGrabber, PinDirection.Input, 0);
            try { Check(_graph.Connect(audioOut, grabberIn)); }
            finally { Marshal.ReleaseComObject(grabberIn); }

            var connected = new AMMediaType();
            Check(grabber.GetConnectedMediaType(connected));
            try
            {
                if (connected.formatType == FormatType.WaveEx && connected.formatPtr != IntPtr.Zero)
                {
                    var wfx = (WaveFormatEx)Marshal.PtrToStructure(connected.formatPtr, typeof(WaveFormatEx))!;
                    Audio.Configure(wfx.nSamplesPerSec, wfx.nChannels, wfx.wBitsPerSample);
                    Log.Info($"Preview audio {wfx.nSamplesPerSec} Hz, {wfx.nChannels} ch, {wfx.wBitsPerSample}-bit");
                }
            }
            finally { DsUtils.FreeAMMediaType(connected); }

            IPin grabberOut = DsFindPin.ByDirection(_audioGrabber, PinDirection.Output, 0);
            try
            {
                int hr = _graph.Render(grabberOut);
                Log.Info($"Preview audio via meter {Hex(hr)}");
            }
            finally { Marshal.ReleaseComObject(grabberOut); }
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Warn($"Audio meter unavailable: {ex.Message}");
            if (_audioGrabber is not null)
            {
                try { _graph?.RemoveFilter(_audioGrabber); } catch { }
                Release(ref _audioGrabber);
            }
            Audio.Reset();
            return false;
        }
    }

    /// <summary>Smart Tee's named pin; for a tee without names (Infinite Pin Tee), its next free output.</summary>
    private IPin TeePin(string name, int index) =>
        DsFindPin.ByName(_smartTee!, name) ?? FindUnconnectedOutput(_smartTee!) ?? DsFindPin.ByDirection(_smartTee!, PinDirection.Output, index)
        ?? throw new COMException($"The tee has no {name} pin.", VFW_E_CANNOT_CONNECT);

    private static IPin? FindUnconnectedOutput(IBaseFilter filter)
    {
        if (filter.EnumPins(out IEnumPins pins) < 0) return null;
        try
        {
            var one = new IPin[1];
            while (pins.Next(1, one, IntPtr.Zero) == 0)
            {
                IPin pin = one[0];
                if (pin.QueryDirection(out PinDirection dir) >= 0 && dir == PinDirection.Output)
                {
                    int hr = pin.ConnectedTo(out IPin other);
                    if (other is not null) Marshal.ReleaseComObject(other);
                    if (hr < 0) return pin;
                }
                Marshal.ReleaseComObject(pin);
            }
            return null;
        }
        finally { Marshal.ReleaseComObject(pins); }
    }

    private int ConnectAvOutTo(IBaseFilter target)
    {
        IPin? avOut = FindOutputPin(_source!, MediaType.Interleaved);
        if (avOut is null)
        {
            Log.Warn("Camcorder has no interleaved DV output pin");
            return VFW_E_CANNOT_CONNECT;
        }
        IPin targetIn = DsFindPin.ByDirection(target, PinDirection.Input, 0);
        try
        {
            int hr = _graph!.ConnectDirect(avOut, targetIn, null);
            Log.Info($"Connect DV A/V Out {Hex(hr)}");
            return hr;
        }
        finally
        {
            Marshal.ReleaseComObject(avOut);
            Marshal.ReleaseComObject(targetIn);
        }
    }

    private void ConnectPins(IPin? output, IPin? input, bool direct = false)
    {
        try
        {
            if (output is null || input is null) throw new COMException("A filter pin is missing.", VFW_E_CANNOT_CONNECT);
            Check(direct ? _graph!.ConnectDirect(output, input, null) : _graph!.Connect(output, input));
        }
        finally
        {
            if (output is not null) Marshal.ReleaseComObject(output);
            if (input is not null) Marshal.ReleaseComObject(input);
        }
    }

    private static IPin? FindOutputPin(IBaseFilter filter, Guid majorType)
    {
        if (filter.EnumPins(out IEnumPins pins) < 0) return null;
        try
        {
            var one = new IPin[1];
            while (pins.Next(1, one, IntPtr.Zero) == 0)
            {
                IPin pin = one[0];
                if (pin.QueryDirection(out PinDirection dir) >= 0 && dir == PinDirection.Output && HasMajorType(pin, majorType))
                    return pin;
                Marshal.ReleaseComObject(pin);
            }
            return null;
        }
        finally { Marshal.ReleaseComObject(pins); }
    }

    private static IPin? FindUnconnectedInput(IBaseFilter filter)
    {
        if (filter.EnumPins(out IEnumPins pins) < 0) return null;
        try
        {
            var one = new IPin[1];
            while (pins.Next(1, one, IntPtr.Zero) == 0)
            {
                IPin pin = one[0];
                if (pin.QueryDirection(out PinDirection dir) >= 0 && dir == PinDirection.Input)
                {
                    int hr = pin.ConnectedTo(out IPin other);
                    if (other is not null) Marshal.ReleaseComObject(other);
                    if (hr < 0) return pin;
                }
                Marshal.ReleaseComObject(pin);
            }
            return null;
        }
        finally { Marshal.ReleaseComObject(pins); }
    }

    private static bool HasMajorType(IPin pin, Guid majorType)
    {
        if (pin.EnumMediaTypes(out IEnumMediaTypes types) < 0) return false;
        try
        {
            var one = new AMMediaType[1];
            while (types.Next(1, one, IntPtr.Zero) == 0)
            {
                bool match = one[0].majorType == majorType;
                DsUtils.FreeAMMediaType(one[0]);
                if (match) return true;
            }
            return false;
        }
        finally { Marshal.ReleaseComObject(types); }
    }

    private void Teardown()
    {
        try { _control?.Stop(); } catch (Exception ex) { Log.Warn($"Graph stop: {ex.Message}"); }
        try { (_grabber as ISampleGrabber)?.SetCallback(null, 1); } catch { }
        _ffmpegPreview?.Dispose();
        _ffmpegPreview = null;
        try { (_audioGrabber as ISampleGrabber)?.SetCallback(null, 1); } catch { }

        if (_capturePath is not null) Log.Info($"Capture closed by teardown: {_capturePath}");
        _capturePath = null;
        _live?.Abort();
        _live?.Dispose();
        _live = null;
        _captureClock.Reset();
        _teeConnected = false;

        // _transport/_tcReader are views of _source; _control/_events are views of _graph.
        _transport = null;
        _tcReader = null;
        _control = null;
        _events = null;
        Release(ref _dropped);
        Release(ref _mux);
        Release(ref _fileWriter);
        Release(ref _captureSplitter);
        Release(ref _nullRenderer);
        Release(ref _audioGrabber);
        Release(ref _grabber);
        Release(ref _decoder);
        Release(ref _previewSplitter);
        Release(ref _smartTee);
        Release(ref _source);
        Release(ref _builder);
        Release(ref _graph);
        Frames.Reset();
        Audio.Reset();
    }

    private void DeviceLost(string reason)
    {
        Log.Warn($"Camcorder lost: {reason}");
        CaptureOutcome? interrupted = _capturePath is not null ? StopCapture(restartPreview: false) : null;
        StopTimers();
        Teardown();
        Raise(new ConnectionInfo(ConnectionState.NoCamcorder, _deviceName,
            "The camcorder stopped responding. Check the FireWire cable and that the camcorder is still on.", reason));
        if (interrupted is { Ok: true })
            CaptureEndedAutomatically?.Invoke(interrupted with { Message = "The camcorder disconnected, so the capture stopped. Everything up to that point was saved." });
        StartRescan();
    }

    // ---------------------------------------------------------------- capture

    private CaptureOutcome StartCapture(string path, bool liveMp4)
    {
        if (_graph is null || _control is null || !_teeConnected)
            return new(false, Message: "Capture isn't available because the camcorder's video couldn't be opened.");
        if (_capturePath is not null) return new(false, Message: "A capture is already running.");

        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
        catch (Exception ex) { return new(false, Message: $"Couldn't use that folder: {ex.Message}"); }

        _control.Stop();
        try
        {
            _captureSplitter = (IBaseFilter)new DVSplitter();
            Check(_graph.AddFilter(_captureSplitter, "DV Splitter (capture)"));
            ConnectPins(TeePin("Capture", 0), DsFindPin.ByDirection(_captureSplitter, PinDirection.Input, 0), direct: true);

            Check(_builder!.SetOutputFileName(MediaSubType.Avi, path, out _mux, out IFileSinkFilter sink));
            _fileWriter = (IBaseFilter)sink;

            // Type-2 DV AVI: the untouched DV video stream plus the first PCM audio pair.
            ConnectPins(DsFindPin.ByDirection(_captureSplitter, PinDirection.Output, 0), FindUnconnectedInput(_mux));
            IPin? audio = DsFindPin.ByDirection(_captureSplitter, PinDirection.Output, 1);
            if (audio is not null)
            {
                try { ConnectPins(audio, FindUnconnectedInput(_mux)); }
                catch (COMException ex) { Log.Warn($"Capture audio not connected: {ex.Message}"); }
            }

            _dropped = FindDroppedFrames();
            _droppedBaseline = 0;
            if (_dropped is not null && _dropped.GetNumDropped(out int baseline) >= 0) _droppedBaseline = baseline;

            // The live MP4 is fed from the preview's copies of the same DV frames, so the capture branch is unchanged.
            bool liveUnavailable = false;
            if (liveMp4)
            {
                if (_ffmpegPreview is not null && PostProcess.FindFfmpeg() is string ffmpeg)
                {
                    _live = new LiveMp4Encoder(ffmpeg, LivePartPath(path));
                    _ffmpegPreview.Live = _live;
                }
                else
                {
                    liveUnavailable = true;
                    Log.Warn("Live MP4 asked for, but the preview isn't using FFmpeg; the MP4 will be made after the capture");
                }
            }

            _capturePath = path;
            _notPlayingPolls = 0;
            _captureSawPlaying = _mode == TransportMode.Playing;
            Check(_control.Run());
            _captureClock.Restart();
            _captureStartTicks = _captureGrewTicks = Environment.TickCount64;
            _captureBytes = 0;
            Log.Info($"Capture started: {path}{(_live is not null ? " (with live MP4)" : "")}");
            return new(true, path, LiveMp4Unavailable: liveUnavailable);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Error("Capture start failed", ex);
            DetachLive()?.Abort();
            _capturePath = null;
            RemoveCaptureBranch();
            try { _control.Run(); } catch { }
            TryDeleteEmpty(path);
            return new(false, Message: IsBusy(ex.HResult)
                ? "Another program is capturing from the camcorder. Close it, then try again."
                : $"The capture couldn't start (code {Hex(ex.HResult)}). Try again, or turn the camcorder off and on.");
        }
    }

    private CaptureOutcome StopCapture(bool restartPreview)
    {
        if (_capturePath is null) return new(false, Message: "No capture is running.");
        string path = _capturePath;

        // Stopping the graph makes the AVI Mux write its index and close the file.
        try { _control?.Stop(); } catch (Exception ex) { Log.Error("Graph stop during capture", ex); }
        LiveMp4Encoder? live = DetachLive(); // no more frames reach it; it finishes in the background
        TimeSpan elapsed = _captureClock.Elapsed;
        _captureClock.Reset();
        int dropped = ReadDropped();
        _capturePath = null;
        RemoveCaptureBranch();

        long bytes = FileLength(path);
        if (restartPreview && _control is not null)
        {
            try { _control.Run(); } catch (Exception ex) { Log.Error("Preview restart", ex); }
        }

        // A file with no frames in it (only the AVI headers) is just clutter: remove it and say so.
        if (bytes < EmptyCaptureBytes)
        {
            Log.Info($"Capture had no picture ({bytes} bytes); deleting {path}");
            try { File.Delete(path); } catch (Exception ex) { Log.Error("Deleting empty capture", ex); }
            live?.Abort();
            live?.Dispose();
            return new(false, null, "Nothing was recorded because no picture came from the tape.", elapsed, 0, dropped);
        }

        Log.Info($"Capture saved: {path} {bytes} bytes, {elapsed}, dropped {dropped}");
        return new(true, path, null, elapsed, bytes, dropped, live);
    }

    /// <summary>"Tape 1.avi" → "Tape 1.mp4.part" (Jellyfin and players ignore it until it becomes "Tape 1.mp4").</summary>
    public static string LivePartPath(string aviPath) =>
        Path.Combine(Path.GetDirectoryName(aviPath)!, Path.GetFileNameWithoutExtension(aviPath) + ".mp4.part");

    private LiveMp4Encoder? DetachLive()
    {
        if (_ffmpegPreview is not null) _ffmpegPreview.Live = null;
        var live = _live;
        _live = null;
        return live;
    }

    /// <summary>Under ~8 DV frames: nothing but AVI headers.</summary>
    private const long EmptyCaptureBytes = 1_000_000;

    private void RemoveCaptureBranch()
    {
        foreach (var filter in new[] { _mux, _fileWriter, _captureSplitter })
        {
            if (filter is null || _graph is null) continue;
            try { _graph.RemoveFilter(filter); } catch { }
        }
        Release(ref _dropped);
        Release(ref _mux);
        Release(ref _fileWriter);
        Release(ref _captureSplitter);
    }

    private IAMDroppedFrames? FindDroppedFrames()
    {
        try
        {
            int hr = _builder!.FindInterface(PinCategory.Capture, MediaType.Interleaved, _source!,
                typeof(IAMDroppedFrames).GUID, out object found);
            if (hr >= 0 && found is IAMDroppedFrames d) return d;
        }
        catch { }
        return _source as IAMDroppedFrames;
    }

    private int ReadDropped()
    {
        try
        {
            return _dropped is not null && _dropped.GetNumDropped(out int n) >= 0 ? Math.Max(0, n - _droppedBaseline) : 0;
        }
        catch { return 0; }
    }

    private void UpdateCapture(TransportMode mode)
    {
        if (_capturePath is null) return;
        long bytes = FileLength(_capturePath);
        if (bytes != _captureBytes)
        {
            _captureBytes = bytes;
            _captureGrewTicks = Environment.TickCount64;
        }
        TimeSpan blank = BlankFor();
        CaptureProgressed?.Invoke(new CaptureProgress(_captureClock.Elapsed, bytes, ReadDropped(), blank, _live?.Dropped ?? -1));

        // Unrecorded tape shows as a flat grey, blue or black picture (or, on MiniDV, no picture at all).
        // After two minutes of that the recording is over: stop, and stop the tape so it doesn't play on to the end.
        if (blank >= BlankStopAfter)
        {
            Log.Info($"Picture blank for {blank}; stopping the capture and the tape");
            var outcome = StopCapture(restartPreview: true);
            if (mode is TransportMode.Playing && _transport is not null)
            {
                try { Log.Info($"Tape stop after blank -> {Hex(_transport.put_Mode(ExtTransportModes.Stop))}"); }
                catch (Exception ex) { Log.Error("Tape stop after blank", ex); }
            }
            CaptureEndedAutomatically?.Invoke(outcome with
            {
                Message = outcome.Ok
                    ? "The tape was blank for 2 minutes, so the capture and the tape stopped. Everything before that was saved."
                    : "The tape was blank for 2 minutes, so the capture stopped. Nothing was recorded.",
            });
            return;
        }

        // Stop on our own when the tape stops, winds or reaches its end, so a forgotten capture doesn't fill
        // the drive. Some camcorders ignore Play over FireWire, so until the tape has played at all we wait
        // longer for someone to press Play on the camcorder itself.
        bool tapeMoving = mode is TransportMode.Playing or TransportMode.Paused or TransportMode.Unknown;
        if (mode is TransportMode.Playing) _captureSawPlaying = true;
        _notPlayingPolls = tapeMoving ? 0 : _notPlayingPolls + 1;
        bool neverStarted = !_captureSawPlaying && _captureClock.Elapsed > TimeSpan.FromSeconds(20);
        bool stoppedAfterPlaying = _captureSawPlaying && _notPlayingPolls >= 5;
        if (neverStarted || stoppedAfterPlaying)
        {
            var outcome = StopCapture(restartPreview: true);
            string reason = neverStarted
                ? "The tape didn't start, so nothing was recorded. Press Play on the camcorder, then Capture again."
                : outcome.Ok ? "The tape stopped, so the capture stopped too." : outcome.Message ?? "The tape stopped.";
            CaptureEndedAutomatically?.Invoke(outcome with { Message = reason });
            return;
        }

        if (_pollCount % 10 == 0 && FreeBytes(_capturePath) is long free && free < AppSettings.MinimumFreeBytes / 2)
        {
            var outcome = StopCapture(restartPreview: true);
            CaptureEndedAutomatically?.Invoke(outcome with { Message = "The drive is almost full, so the capture stopped." });
        }
    }

    /// <summary>
    /// How long the capture has gone without a real picture. Zero until the preview has delivered a frame during
    /// this capture, so a capture without a working preview is never stopped for being "blank".
    /// </summary>
    private TimeSpan BlankFor()
    {
        long now = Environment.TickCount64;
        if (Frames.LastFrameTicks < _captureStartTicks) return TimeSpan.Zero;
        // No preview frames lately but the file is still growing: the preview stalled, not the tape. Don't guess.
        if (now - Frames.LastFrameTicks > 3000 && now - _captureGrewTicks < 3000) return TimeSpan.Zero;
        long since = Math.Max(Frames.LastPictureTicks, _captureStartTicks);
        return TimeSpan.FromMilliseconds(Math.Max(0, now - since));
    }

    private static long FileLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static long? FreeBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; } catch { return null; }
    }

    private static void TryDeleteEmpty(string path)
    {
        try { if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path); } catch { }
    }

    // ---------------------------------------------------------------- polling

    private void Poll()
    {
        if (_transport is null) return;
        DrainEvents();
        if (_transport is null) return;

        int hr = _transport.get_Mode(out ExtTransportModes raw);
        if (hr < 0)
        {
            if (++_pollFailures >= MaxPollFailures) DeviceLost($"Transport status failed {Hex(hr)}");
            return;
        }
        _pollFailures = 0;

        if (raw != _lastRawMode)
        {
            Log.Info($"Transport mode {raw} ({(int)raw})");
            _lastRawMode = raw;
        }
        _mode = MapMode(raw);

        TapeTime? tc = _timecode.Read(_tcReader);
        TapeTime? counter = _counter.Read(_tcReader);
        if (_pollCount++ % 10 == 0) _tapePresent = ReadTapePresent();

        SnapshotUpdated?.Invoke(new TransportSnapshot(_mode, (int)raw, tc, counter, _tapePresent));
        UpdateCapture(_mode);
    }

    private bool? ReadTapePresent()
    {
        try
        {
            return _transport!.GetStatus(ExtTransportStatus.MediaPresent, out int value) >= 0 ? value != 0 : null;
        }
        catch { return null; }
    }

    private void DrainEvents()
    {
        if (_events is null) return;
        while (_events.GetEvent(out EventCode code, out IntPtr p1, out IntPtr p2, 0) >= 0)
        {
            _events.FreeEventParams(code, p1, p2);
            if (code == EventCode.DeviceLost && p2 == IntPtr.Zero)
            {
                DeviceLost("Windows reported the device was removed");
                return;
            }
            if (code is EventCode.ErrorAbort or EventCode.ErrorAbortEx)
                Log.Warn($"Graph error event {code} {Hex((int)p1)}");
        }
    }

    private static TransportMode MapMode(ExtTransportModes raw) => raw switch
    {
        ExtTransportModes.Play => TransportMode.Playing,
        ExtTransportModes.Stop => TransportMode.Stopped,
        ExtTransportModes.Freeze => TransportMode.Paused,
        ExtTransportModes.FF or ExtTransportModes.Wind or ExtTransportModes.PlayFastestFwd
            or ExtTransportModes.PlaySlowestFwd => TransportMode.FastForward,
        ExtTransportModes.Rew or ExtTransportModes.RewFastest or ExtTransportModes.PlayFastestRev
            or ExtTransportModes.PlaySlowestRev or ExtTransportModes.RevPlay => TransportMode.Rewind,
        ExtTransportModes.Record or ExtTransportModes.RecordFreeze => TransportMode.Recording,
        _ => TransportMode.Unknown,
    };

    // ---------------------------------------------------------------- commands

    private CommandResult Send(TransportCommand command)
    {
        if (_transport is null) return new(false, "The camcorder isn't connected.");

        ExtTransportModes mode = command switch
        {
            TransportCommand.Play => ExtTransportModes.Play,
            TransportCommand.Pause => ExtTransportModes.Freeze,
            TransportCommand.Stop => ExtTransportModes.Stop,
            TransportCommand.FastForward => ExtTransportModes.FF,
            TransportCommand.Rewind => ExtTransportModes.Rew,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

        int hr = _transport.put_Mode(mode);
        Log.Info($"Command {command} -> {Hex(hr)}");

        // Many Digital8 camcorders refuse to wind straight from play or pause. Stop, then retry.
        if (hr < 0 && command is TransportCommand.FastForward or TransportCommand.Rewind)
        {
            _transport.put_Mode(ExtTransportModes.Stop);
            Thread.Sleep(400);
            hr = _transport.put_Mode(mode);
            Log.Info($"Retry {command} after stop -> {Hex(hr)}");
        }

        Poll();
        return hr >= 0 ? new(true) : new(false, DescribeCommandFailure(command, hr));
    }

    private static string DescribeCommandFailure(TransportCommand command, int hr)
    {
        string name = command switch
        {
            TransportCommand.FastForward => "fast-forward",
            _ => command.ToString().ToLowerInvariant(),
        };
        return hr switch
        {
            ERROR_NOT_READY => $"The camcorder isn't ready to {name}. Check that a tape is loaded.",
            ERROR_TIMEOUT or ERROR_SEM_TIMEOUT =>
                $"The camcorder didn't answer the {name} command. Check that it's on and set to play (VCR) mode.",
            _ => $"The camcorder didn't accept {name} (code {Hex(hr)}). Try Stop first, or use the button on the camcorder.",
        };
    }

    // ---------------------------------------------------------------- timers & helpers

    private void StartPolling()
    {
        _pollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(PollIntervalMs), DispatcherPriority.Background,
            (_, _) => SafePoll(), _thread.Dispatcher);
        _pollTimer.Start();
    }

    private void SafePoll()
    {
        try { Poll(); }
        catch (Exception ex)
        {
            Log.Error("Poll failed", ex);
            if (++_pollFailures >= MaxPollFailures) DeviceLost(ex.Message);
        }
    }

    private void StartRescan()
    {
        if (_disposed) return;
        _rescanTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(RescanIntervalMs), DispatcherPriority.Background,
            (_, _) => Rescan(), _thread.Dispatcher);
        _rescanTimer.Start();
    }

    /// <summary>Only checks whether a camcorder has appeared; never opens it until one has.</summary>
    private void Rescan()
    {
        var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        bool present = devices.Any(IsDvDevice);
        foreach (var d in devices) d.Dispose();
        if (present) Connect(quiet: false);
    }

    private void StopTimers()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        _rescanTimer?.Stop();
        _rescanTimer = null;
    }

    private void Raise(ConnectionInfo info)
    {
        if (info == _lastInfo) return;
        _lastInfo = info;
        Log.Info($"State {info.State} {info.DeviceName} {info.TechnicalDetail}");
        ConnectionChanged?.Invoke(info);
    }

    private static bool IsBusy(int hr) =>
        hr is ERROR_TOO_MANY_OPEN_FILES or ERROR_BUSY or ERROR_SHARING_VIOLATION or ERROR_NO_SYSTEM_RESOURCES;

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static string Hex(int hr) => $"0x{hr:X8}";

    private static void Release<T>(ref T? obj) where T : class
    {
        if (obj is not null && Marshal.IsComObject(obj))
        {
            try { Marshal.FinalReleaseComObject(obj); } catch { }
        }
        obj = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            _thread.Invoke(() =>
            {
                _disposed = true;
                StopTimers();
                if (_capturePath is not null) StopCapture(restartPreview: false);
                Teardown();
            }, TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { Log.Error("Shutdown", ex); }
        _thread.Dispose();
    }

    /// <summary>
    /// One IAMTimecodeReader reading (tape timecode or camcorder counter). If a camcorder never
    /// answers a reading, we back off to one try every few seconds to keep the AV/C bus quiet.
    /// </summary>
    private sealed class Reading(string name, int flag)
    {
        private bool _everWorked;
        private int _failures;
        private int _skip;
        private bool _lastValid;
        private bool _loggedRaw;

        public void Reset()
        {
            _everWorked = false;
            _failures = 0;
            _skip = 0;
            _lastValid = false;
            _loggedRaw = false;
        }

        public TapeTime? Read(IAMTimecodeReader? reader)
        {
            if (reader is null) return null;
            // Never answered after 5 tries: only ask every 25 polls (5 s) so transport commands aren't queued behind it.
            if (!_everWorked && _failures >= 5 && _skip++ % 25 != 0) return null;

            var sample = new TimecodeSample { dwFlags = flag };
            int hr;
            try { hr = reader.GetTimecode(ref sample); }
            catch (Exception ex) { hr = ex.HResult; }

            if (hr < 0)
            {
                if (++_failures == 5 && !_everWorked) Log.Info($"Camcorder doesn't report {name} ({Hex(hr)}); backing off");
                return null;
            }

            _everWorked = true;
            _failures = 0;
            TapeTime? value = TapeTime.FromBcd(sample.dwFrames);
            bool valid = value is not null;
            if (!_loggedRaw || valid != _lastValid)
            {
                Log.Info($"{name}: raw 0x{sample.dwFrames:X8} -> {(valid ? value.ToString() : "none")}");
                _loggedRaw = true;
                _lastValid = valid;
            }
            return value;
        }
    }
}
