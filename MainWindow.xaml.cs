using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TapeRescue.Controls;
using TapeRescue.Services;

namespace TapeRescue;

public partial class MainWindow : Window
{
    private enum ToastKind { Success, Warning, Error }

    private const int MeterBars = 14;

    private readonly TransportController _controller = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _spaceTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly Button[] _transportButtons;
    private readonly Border[] _meter = new Border[MeterBars];
    private readonly ObservableCollection<string> _plugins;
    private readonly string? _topazPath = PostProcess.FindTopaz();
    private readonly bool _hasFfmpeg = FfmpegTools.FindFfmpeg() is not null;
    private CancellationTokenSource? _postCts;
    private bool _postRunning;
    private bool _liveMp4Saving; // the live MP4 made during capture hasn't been saved as "Name.mp4" yet

    private WriteableBitmap? _video, _glass;
    private ConnectionInfo _connection = new(ConnectionState.Searching);
    private TransportMode _mode = TransportMode.Unknown;
    private bool _connected, _previewAvailable, _captureAvailable;
    private bool _capturing, _captureBusy, _hasPicture;
    private bool _captureLiveWanted; // MP4 live convert was on when this capture started
    private string? _pluginChoice;
    private string _formatKey = "";
    private float _meterLevel;
    private int _litBars = -1;
    private Action? _toastAction;

    public MainWindow()
    {
        InitializeComponent();
        _transportButtons = [RewindButton, PlayButton, ForwardButton, StopButton];

        for (int i = 0; i < MeterBars; i++)
        {
            _meter[i] = new Border
            {
                Margin = new Thickness(1.5, 0, 1.5, 0),
                CornerRadius = new CornerRadius(2),
                Background = Res(i >= MeterBars - 2 ? "B.MeterHot" : "B.Meter"),
                Opacity = 0.14,
            };
            MeterGrid.Children.Add(_meter[i]);
        }

        _plugins = new ObservableCollection<string>(_settings.AudioPlugins);
        PluginList.ItemsSource = _plugins;
        UpdatePluginUi();
        UpdateAiUi();
        UpdateChapterUi();
        UpdateConvertUi();
        UpdateLiveMp4Ui();
        UpdateMuteUi();
        if (_settings.MutePreview) _ = _controller.SetPreviewMutedAsync(true); // applied whenever the camcorder connects

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        FooterVersion.Text = version is null ? "TAPE TIME MACHINE" : $"TAPE TIME MACHINE v{version.Major}.{version.Minor}.{version.Build}";

        FileNameBox.Text = _settings.FileName;
        SummaryName.Text = _settings.FileName;
        UpdateFolderUi();
        UpdateFreeSpace();

        _controller.ConnectionChanged += info => Dispatcher.InvokeAsync(() => OnConnectionChanged(info));
        _controller.SnapshotUpdated += snapshot => Dispatcher.InvokeAsync(() => OnSnapshot(snapshot));
        _controller.CaptureProgressed += progress => Dispatcher.InvokeAsync(() => OnCaptureProgress(progress));
        _controller.CaptureEndedAutomatically += outcome => Dispatcher.InvokeAsync(() =>
        {
            SetCapturing(false);
            OnCaptureFinished(outcome);
        });

        _spaceTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => UpdateFreeSpace(), Dispatcher);
        _toastTimer = new DispatcherTimer();
        _toastTimer.Tick += (_, _) => HideToast();

        CompositionTarget.Rendering += OnRendering;
        StateChanged += (_, _) => OnWindowStateChanged();
        Loaded += async (_, _) =>
        {
            OnConnectionChanged(_connection);
#if DEBUG
            if (DemoRequested)
            {
                StartDemo();
                return;
            }
            TransportController.DebugPreviewVariant = PreviewVariantArg;
#endif
            await _controller.ConnectAsync();
#if DEBUG
            if (SelfTestFolder is string selfTest) _ = RunSelfTestAsync(selfTest);
#endif
        };
        Closing += OnClosing;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    private Geometry Geo(string key) => (Geometry)FindResource(key);
    private Brush Res(string key) => (Brush)FindResource(key);
    private static bool AnimationsAllowed => SystemParameters.ClientAreaAnimation;

    // ---------------------------------------------------------------- picture, signal, meter

    private void OnRendering(object? sender, EventArgs e)
    {
        _controller.Frames.TryConsume(WriteFrame);
        bool hasPicture = _connected && _controller.Frames.MillisecondsSinceLastFrame < 1500;
        if (hasPicture != _hasPicture)
        {
            _hasPicture = hasPicture;
            UpdatePictureState();
        }
        UpdateMeter();
        UpdateFormatInfo();
    }

    private void WriteFrame(in FrameData f)
    {
        EnsureBitmap(ref _video, f.Width, f.Height, b => VideoImage.Source = b);
        EnsureBitmap(ref _glass, f.GlassWidth, f.GlassHeight, b =>
        {
            TransportDock.BackdropSource = b;
            Toast.BackdropSource = b;
        });
        _video!.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Full, f.Width * 4, 0);
        _glass!.WritePixels(new Int32Rect(0, 0, f.GlassWidth, f.GlassHeight), f.Glass, f.GlassWidth * 4, 0);
    }

    private static void EnsureBitmap(ref WriteableBitmap? bitmap, int width, int height, Action<WriteableBitmap> assign)
    {
        if (bitmap is not null && bitmap.PixelWidth == width && bitmap.PixelHeight == height) return;
        bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        assign(bitmap);
    }

    private void UpdatePictureState()
    {
        VideoImage.Visibility = _hasPicture ? Visibility.Visible : Visibility.Hidden;
        EmptyState.Visibility = _connected && !_hasPicture ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = !_previewAvailable
            ? "Preview isn't available, but the buttons still control the camcorder."
            : _mode is TransportMode.FastForward or TransportMode.Rewind
                ? "The picture comes back when the tape plays."
                : "Press Play to see the tape.";

        int lit = _hasPicture ? 4 : 0;
        System.Windows.Shapes.Rectangle[] bars = [Bar1, Bar2, Bar3, Bar4];
        for (int i = 0; i < bars.Length; i++) bars[i].Fill = Res(i < lit ? "B.Dot" : "B.Unlit");
        SignalWord.Text = _hasPicture ? "GOOD" : _connected ? "NONE" : "OFF";
        SignalWord.Foreground = _hasPicture ? Res("B.Accent") : Res("B.TextDim");
    }

    /// <summary>A real peak meter from the preview audio, with a gentle fall-off.</summary>
    private void UpdateMeter()
    {
        float peak = _connected ? _controller.Audio.Peak : 0f;
        _meterLevel = Math.Max(peak, _meterLevel * 0.9f);
        int lit = 0;
        if (_meterLevel > 0.0001f)
        {
            double db = 20 * Math.Log10(_meterLevel);
            lit = (int)Math.Round(Math.Clamp((db + 48) / 48, 0, 1) * MeterBars);
        }
        if (lit == _litBars) return;
        _litBars = lit;
        for (int i = 0; i < MeterBars; i++) _meter[i].Opacity = i < lit ? 1 : 0.14;
    }

    private void UpdateFormatInfo()
    {
        int w = _controller.Frames.Width, h = _controller.Frames.Height;
        var audio = _controller.Audio;
        string key = $"{_connected}|{w}x{h}|{audio.SampleRate}|{audio.Channels}";
        if (key == _formatKey) return;
        _formatKey = key;

        string? standard = h switch { 480 => "NTSC", 576 => "PAL", _ => null };
        FormatText.Text = _connected && standard is not null ? $"DV / {standard}" : "DV";
        VideoInfo.Text = _connected && w > 0
            ? $"{w} × {h} · {(h == 576 ? "25" : "29.97")} FPS"
            : "—";
        AudioInfo.Text = _connected && audio.SampleRate > 0
            ? $"{audio.Channels switch { 1 => "MONO", 2 => "STEREO", _ => $"{audio.Channels} CH" }} · {audio.SampleRate / 1000.0:0.#} KHZ"
            : "—";
    }

    private void Frame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Frame.Clip = new RectangleGeometry(new Rect(e.NewSize), 19, 19);
    }

    // ---------------------------------------------------------------- connection

    private void OnConnectionChanged(ConnectionInfo info)
    {
        _connection = info;
        _connected = info.State == ConnectionState.Connected;
        _previewAvailable = info.PreviewAvailable;
        _captureAvailable = info.CaptureAvailable;
        if (!_connected && _capturing) SetCapturing(false); // the controller reports what was saved
        TransportButtons.IsEnabled = _connected && !_capturing;

        switch (info.State)
        {
            case ConnectionState.Searching:
                SourceName.Text = "Looking for camcorder";
                ShowMessage("Geo.Refresh", "B.TextSecondary", "Looking for your camcorder", "This takes a few seconds.", [], retry: false, detail: null);
                break;

            case ConnectionState.Connected:
                SourceName.Text = info.DeviceName ?? "Camcorder";
                HideMessage();
                break;

            case ConnectionState.NoCamcorder:
                SourceName.Text = "No camcorder";
                ShowMessage("Geo.Warning", "B.Yellow", "Camcorder not found",
                    info.Message ?? "The FireWire port is working, but no camcorder is answering.",
                    [
                        "1. Plug the FireWire (DV) cable into the camcorder and the PC.",
                        "2. Turn the camcorder on and set it to Play/Edit, not Camera.",
                        "3. Close WinDV or any other capture program.",
                    ],
                    retry: true, detail: "Tape Time Machine checks again every few seconds.");
                break;

            case ConnectionState.NoFirewire:
                SourceName.Text = "No FireWire port";
                ShowMessage("Geo.ErrorCircle", "B.Red", "No FireWire port found",
                    "Digital8 camcorders send video over FireWire, and this PC has no working FireWire (IEEE 1394) port.",
                    [
                        "1. Fit a FireWire card. Cards with a Texas Instruments chip work best.",
                        "2. Check it appears in Device Manager under IEEE 1394 host controllers.",
                    ],
                    retry: true, detail: null);
                break;

            case ConnectionState.FirewireDriverProblem:
                SourceName.Text = "FireWire driver problem";
                ShowMessage("Geo.ErrorCircle", "B.Red", "FireWire driver problem",
                    "Windows found the FireWire port, but its driver isn't working.",
                    [
                        "1. Open Device Manager (devmgmt.msc).",
                        "2. Under IEEE 1394 host controllers, choose Update driver.",
                        "3. Pick \"1394 OHCI Compliant Host Controller\", then restart.",
                    ],
                    retry: true, detail: info.TechnicalDetail);
                break;

            case ConnectionState.DeviceBusy:
                SourceName.Text = info.DeviceName ?? "Camcorder";
                ShowMessage("Geo.Warning", "B.Yellow", "Camcorder is busy",
                    "Another program is capturing from the camcorder, so Tape Time Machine is leaving it alone.",
                    ["When that capture has finished, close the other program and select Check again."],
                    retry: true, detail: info.TechnicalDetail);
                break;

            default:
                SourceName.Text = info.DeviceName ?? "Camcorder";
                ShowMessage("Geo.ErrorCircle", "B.Red", "Couldn't open the camcorder",
                    info.Message ?? "Windows reported an error while opening the camcorder.",
                    [
                        "1. Turn the camcorder off and on again.",
                        "2. Unplug the FireWire cable for a few seconds, then plug it back in.",
                    ],
                    retry: true, detail: info.TechnicalDetail);
                break;
        }

        if (!_connected)
        {
            _mode = TransportMode.Unknown;
            SetTimecode(null);
        }
        UpdateModeOsd();
        UpdateStatusLine();
        UpdateCaptureButton();
        UpdatePictureState();
    }

    private void UpdateStatusLine()
    {
        (string brush, string text, string footer) = _connection.State switch
        {
            ConnectionState.Searching => ("B.TextDim", "Looking for your camcorder", "LOOKING FOR DEVICE"),
            ConnectionState.Connected when _capturing => ("B.RecDot", "Capturing", "CAPTURING"),
            ConnectionState.Connected when _captureAvailable => ("B.Dot", "Ready to capture", "DEVICE CONNECTED"),
            ConnectionState.Connected => ("B.Yellow", "Tape control only, no video", "DEVICE CONNECTED"),
            ConnectionState.DeviceBusy => ("B.Yellow", "Busy in another program", "DEVICE BUSY"),
            ConnectionState.NoCamcorder => ("B.Yellow", "Camcorder not found", "NO DEVICE"),
            ConnectionState.NoFirewire => ("B.Red", "No FireWire port", "NO FIREWIRE PORT"),
            ConnectionState.FirewireDriverProblem => ("B.Red", "FireWire driver problem", "DRIVER PROBLEM"),
            _ => ("B.Red", "Couldn't open the camcorder", "DEVICE ERROR"),
        };
        StatusText.Text = text;
        FooterStatus.Text = footer;
        SetDot(StatusDot, brush);
        SetDot(FooterDot, brush);
    }

    private void SetDot(System.Windows.Shapes.Ellipse dot, string brushKey)
    {
        var brush = (SolidColorBrush)Res(brushKey);
        dot.Fill = brush;
        dot.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 10, Opacity = 0.85, Color = Color.FromRgb(brush.Color.R, brush.Color.G, brush.Color.B) };
    }

    private void ShowMessage(string geo, string brush, string title, string body, string[] steps, bool retry, string? detail)
    {
        MessagePanel.Visibility = Visibility.Visible;
        MessageIcon.Data = Geo(geo);
        MessageIcon.Foreground = Res(brush);
        MessageTitle.Text = title;
        MessageBody.Text = body;
        MessageSteps.ItemsSource = steps;
        MessageSteps.Visibility = steps.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        MessageDetail.Text = detail ?? "";
        MessageDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        TransportDock.Visibility = Visibility.Collapsed;
    }

    private void HideMessage()
    {
        MessagePanel.Visibility = Visibility.Collapsed;
        TransportDock.Visibility = Visibility.Visible;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await _controller.ConnectAsync();

    // ---------------------------------------------------------------- transport

    private void OnSnapshot(TransportSnapshot s)
    {
        if (!_connected) return;
        bool modeChanged = s.Mode != _mode;
        _mode = s.Mode;

        SetTimecode(s.Timecode ?? s.Counter);
        TimecodeText.ToolTip = (s.Timecode is not null ? "Tape position, from the tape's timecode"
            : s.Counter is not null ? "Tape position, from the camcorder's counter"
            : "This tape isn't reporting a position");

        bool playing = s.Mode == TransportMode.Playing;
        PlayIcon.Data = Geo(playing ? "Geo.Pause" : "Geo.Play");
        PlayIcon.Filled = playing;
        PlayButton.ToolTip = playing ? "Pause (Space)" : "Play (Space)";
        AutomationProperties.SetName(PlayButton, playing ? "Pause" : "Play");

        if (modeChanged)
        {
            UpdateModeOsd();
            UpdatePictureState();
        }
    }

    private void SetTimecode(Models.TapeTime? t)
    {
        TimecodeMain.Text = t is { } v ? $"{v.Hours:00}:{v.Minutes:00}:{v.Seconds:00}" : "--:--:--";
        TimecodeFrames.Text = t is { } f ? $":{f.Frames:00}" : ":--";
    }

    /// <summary>Camcorder-style mode text in the top-left of the picture.</summary>
    private void UpdateModeOsd()
    {
        string? text = _connected ? _mode switch
        {
            TransportMode.Playing => "PLAY",
            TransportMode.Paused => "PAUSE",
            TransportMode.Stopped => "STOP",
            TransportMode.FastForward => "FF ››",
            TransportMode.Rewind => "‹‹ REW",
            TransportMode.Recording => "REC",
            _ => null,
        } : null;
        ModeOsd.Text = text ?? "";
        ModeOsd.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task SendAsync(TransportCommand command)
    {
        if (!_connected || _capturing) return;
        var result = await _controller.SendAsync(command);
        if (!result.Ok)
        {
            ShowToast(result.Message ?? "The camcorder didn't accept that command.", ToastKind.Warning);
            return;
        }
        if (command == TransportCommand.Play)
            await NudgeIfNotPlayingAsync("The camcorder didn't start playing. Press Play on the camcorder itself; Tape Time Machine follows along.");
    }

    /// <summary>
    /// Some camcorders (including the Sony this was tested with) accept Play over FireWire but don't move.
    /// If the tape still isn't playing after a few seconds, say what to do instead of failing silently.
    /// </summary>
    private async Task NudgeIfNotPlayingAsync(string message, bool onlyWhileCapturing = false)
    {
        await Task.Delay(3000);
        if (!_connected || _mode == TransportMode.Playing) return;
        if (onlyWhileCapturing && !_capturing) return;
        ShowToast(message, ToastKind.Warning);
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) =>
        await SendAsync(_mode == TransportMode.Playing ? TransportCommand.Pause : TransportCommand.Play);
    private async void Stop_Click(object sender, RoutedEventArgs e) => await SendAsync(TransportCommand.Stop);
    private async void Rewind_Click(object sender, RoutedEventArgs e) => await SendAsync(TransportCommand.Rewind);
    private async void Forward_Click(object sender, RoutedEventArgs e) => await SendAsync(TransportCommand.FastForward);

    // ---------------------------------------------------------------- capture

    private void UpdateCaptureButton() =>
        CaptureButton.IsEnabled = !_captureBusy && (_capturing || (_connected && _captureAvailable));

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (_captureBusy) return;
        _captureBusy = true;
        UpdateCaptureButton();
        try
        {
            if (_capturing)
            {
                var outcome = await _controller.StopCaptureAsync();
                SetCapturing(false);
                OnCaptureFinished(outcome);
            }
            else
            {
                await StartCaptureAsync();
            }
        }
        finally
        {
            _captureBusy = false;
            UpdateCaptureButton();
        }
    }

    private async Task StartCaptureAsync()
    {
        if (!_connected || !_captureAvailable) return;

        string folder = _settings.OutputFolder;
        if (AppSettings.ValidateFolder(folder) is string problem)
        {
            ShowToast(problem, ToastKind.Warning);
            return;
        }
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex)
        {
            ShowToast($"Couldn't use {folder}. {ex.Message}", ToastKind.Error);
            return;
        }
        if (FreeBytes(folder) is long free && free < AppSettings.MinimumFreeBytes)
        {
            ShowToast("There isn't enough free space on this drive. Choose a folder on another drive.", ToastKind.Warning);
            return;
        }

        string name = AppSettings.CleanFileName(FileNameBox.Text);
        FileNameBox.Text = name;
        _settings.FileName = name;
        _settings.Save();
        string path = AppSettings.UniquePath(folder, name, ".avi");

        HideToast();
        bool live = LiveMp4On;
        var result = await _controller.StartCaptureAsync(path, live);
        if (!result.Ok)
        {
            ShowToast(result.Message ?? "The capture couldn't start.", ToastKind.Error);
            return;
        }
        _captureLiveWanted = live; // if the live MP4 can't run or falls behind, it's made after the capture instead
        SetCapturing(true);

        // One button does it all: start the tape if it isn't already playing.
        if (_mode != TransportMode.Playing)
        {
            var play = await _controller.SendAsync(TransportCommand.Play);
            if (!play.Ok)
                ShowToast("Ready to record. Press Play on the camcorder to start the tape.", ToastKind.Warning);
            else
                await NudgeIfNotPlayingAsync("Ready to record. Press Play on the camcorder to start the tape.", onlyWhileCapturing: true);
        }
    }

    private void SetCapturing(bool on)
    {
        _capturing = on;
        CaptureButton.Style = (Style)FindResource(on ? "CaptureButtonActive" : "CaptureButton");
        CaptureGlyph.Data = Geo(on ? "Geo.StopSmall" : "Geo.Dot");
        CaptureLabel.Text = on ? "STOP" : "CAPTURE";
        CaptureButton.ToolTip = on ? "Stop and save (R)" : "Capture this tape (R)";
        AutomationProperties.SetName(CaptureButton, on ? "Stop capture" : "Start capture");

        RecOsd.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on && AnimationsAllowed)
        {
            RecDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.5, TimeSpan.FromSeconds(0.5))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            RecDot.BeginAnimation(OpacityProperty, null);
        }

        SummaryLabel.Text = on ? "CAPTURING" : "NOW PLAYING";
        TransportButtons.IsEnabled = _connected && !on;
        FolderButton.IsEnabled = !on;
        LiveMp4Button.IsEnabled = !on && _hasFfmpeg;
        DeleteAviButton.IsEnabled = !on;
        FileNameBox.IsReadOnly = on;
        if (on)
        {
            RecText.Text = "REC 00:00:00";
            StorageNote.Text = "This capture: 0 MB · No dropped frames";
        }
        else
        {
            StorageNote.Text = IdleStorageNote;
            StorageNote.Foreground = (Brush)new BrushConverter().ConvertFrom("#6EC1D0E5")!;
        }
        UpdateStatusLine();
        UpdateCaptureButton();
    }

    private void OnCaptureProgress(CaptureProgress p)
    {
        if (!_capturing) return;
        RecText.Text = $"REC {FormatClock(p.Elapsed)}";
        // Warn once the picture has been blank for 30 seconds, counting down to the automatic stop.
        if (p.Blank >= TimeSpan.FromSeconds(30))
        {
            var left = TransportController.BlankStopAfter - p.Blank;
            StorageNote.Text = $"Blank tape · stops in {Math.Max(0, (int)Math.Ceiling(left.TotalSeconds))} s if no picture returns";
            StorageNote.Foreground = Res("B.Yellow");
            return;
        }
        string dropped = p.DroppedFrames == 0 ? "No dropped frames" : $"{p.DroppedFrames:N0} dropped frames";
        // The live MP4 only matters here if it fell behind; the AVI is unaffected and the MP4 is then made afterwards.
        string live = p.LiveMp4Dropped switch
        {
            > 0 => " · MP4 fell behind, will finish after capture",
            0 => " · MP4 live",
            _ => "",
        };
        StorageNote.Text = $"This capture: {FormatBytes(p.Bytes)} · {dropped}{live}";
        StorageNote.Foreground = p.DroppedFrames == 0 && p.LiveMp4Dropped <= 0
            ? (Brush)new BrushConverter().ConvertFrom("#6EC1D0E5")!
            : Res("B.Yellow");
    }

    private async void OnCaptureFinished(CaptureOutcome outcome)
    {
        UpdateFreeSpace();
        bool liveWanted = _captureLiveWanted;
        _captureLiveWanted = false;
        if (!outcome.Ok)
        {
            if (outcome.LiveMp4 is LiveMp4Encoder orphan)
            {
                orphan.Abort();
                orphan.Dispose();
            }
            if (outcome.Message is not null) ShowToast(outcome.Message, ToastKind.Warning);
            return;
        }

        string file = Path.GetFileName(outcome.Path) ?? "the file";
        string dropped = outcome.DroppedFrames > 0 ? $", {outcome.DroppedFrames:N0} dropped frames" : "";
        string lead = outcome.Message is null ? "" : outcome.Message + " ";
        string path = outcome.Path!;
        ShowToast($"{lead}Saved {file} ({FormatClock(outcome.Elapsed)}, {FormatBytes(outcome.Bytes)}{dropped}).",
            ToastKind.Success, "Show in folder", () => ShowInExplorer(path));

        // Ready for the next tape: "Tape 7" becomes "Tape 8".
        string next = AppSettings.NextName(_settings.FileName);
        if (next != _settings.FileName)
        {
            FileNameBox.Text = next;
            _settings.FileName = next;
            _settings.Save();
        }

        await RunPostProcessingAsync(path, outcome.LiveMp4, liveWanted, fromCapture: true, captureDropped: outcome.DroppedFrames);
    }

    /// <summary>
    /// Runs the switched-on steps on a finished file: chapters, audio clean-up, a ready-to-watch copy,
    /// then Topaz. The file itself is never changed; every step writes a new file next to it.
    /// </summary>
    /// <param name="live">The MP4 made during the capture (still finishing), if MP4 live convert was on.</param>
    /// <param name="liveWanted">MP4 live convert was on: make the MP4 now if the live one isn't usable.</param>
    /// <param name="fromCapture">Wait for an earlier tape's processing instead of refusing.</param>
    /// <param name="captureDropped">Frames the capture dropped: the AVI fills them in, the live MP4 never gets them.</param>
    private async Task RunPostProcessingAsync(string capturedPath, LiveMp4Encoder? live = null, bool liveWanted = false, bool fromCapture = false,
        long captureDropped = 0)
    {
        // The live encoder only needs to write its last few seconds; let it do that straight away.
        Task<LiveMp4Result>? liveFinish = live?.FinishAsync();

        if (_postRunning && !fromCapture)
        {
            ShowToast("Post-processing is already running. Let it finish, or stop it first.", ToastKind.Warning);
            return;
        }
        while (_postRunning) await Task.Delay(1000); // a capture finished while the previous tape was still processing
        _postRunning = true;
        ProcessFileButton.IsEnabled = false;
        var cts = _postCts = new CancellationTokenSource();
        CancellationToken ct = cts.Token;
        void Status(string text) { if (_postRunning && !ct.IsCancellationRequested) SetPostStatus(text); }

        var done = new List<string>();
        string current = capturedPath, show = capturedPath;
        string? problem = null;
        string? jellyfinMp4 = null;     // the Jellyfin/phone MP4 once it exists
        LiveMp4Finish? liveMp4 = null;  // set when that MP4 is the live one
        string? liveProblem = null;
        Log.Info($"Post-processing {capturedPath}" + (live is not null ? " (live MP4 to finish)" : ""));
        _liveMp4Saving = liveFinish is not null;
        try
        {
            // MP4 live convert: save the MP4 made during capture first, before the slow steps, so the tape's
            // MP4 exists even if the rest is stopped or the app is closed. Chapters go into it afterwards.
            if (liveFinish is not null)
            {
                Status("Finishing the MP4…");
                LiveMp4Result liveResult = await liveFinish.WaitAsync(ct);
                live?.Dispose();
                ConvertProgressLabel.Text = "SAVING MP4";
                var finishProgress = StepProgress(ConvertProgressPanel, ConvertProgressBar, ConvertProgressText, ConvertTimeLeft, "Saving the MP4", Status);
                var finish = await Converter.FinishLiveMp4Async(liveResult, capturedPath, captureDropped, finishProgress, ct);
                ConvertProgressPanel.Visibility = Visibility.Collapsed;
                if (finish.Ok && finish.OutputPath is string saved)
                {
                    liveMp4 = finish;
                    jellyfinMp4 = show = saved;
                }
                else
                {
                    liveProblem = finish.Message ?? "the live MP4 couldn't be finished";
                    Log.Warn($"Live MP4 not used: {liveProblem}; the AVI will be converted instead");
                }
                _liveMp4Saving = false;
            }

            // Chapters. A chapter file that is already there is kept: its chapters may have been named by hand.
            string chapterFile = ChapterWriter.XmlPathFor(capturedPath);
            List<Chapter> chapters = ChapterWriter.ReadMatroskaXml(chapterFile);
            if (_settings.AddChapters && chapters.Count == 0)
            {
                var scanProgress = StepProgress(ChapterProgressPanel, ChapterProgressBar, ChapterProgressText, ChapterTimeLeft, "Finding scenes", Status);
                var scan = await SceneScanner.ScanAsync(capturedPath, scanProgress, ct);
                ChapterProgressPanel.Visibility = Visibility.Collapsed;
                if (scan.Ok)
                {
                    chapters = ChapterWriter.FromScenes(scan.SceneStarts);
                    ChapterWriter.WriteMatroskaXml(chapterFile, chapters);
                    done.Add(chapters.Count == 1 ? "1 chapter" : $"{chapters.Count} chapters");
                }
                else problem = scan.Message ?? "The scene scan didn't finish.";
            }

            string? cleanedAudio = null;
            if (_settings.AudioPlugins.Count > 0)
            {
                Status("Cleaning audio… 0%");
                var result = await PostProcess.CleanAudioAsync(capturedPath, _settings.AudioPlugins.ToList(),
                    new Progress<double>(p => Status($"Cleaning audio… {p * 100:0}%")), chapters);
                ct.ThrowIfCancellationRequested();
                if (result.Ok && result.OutputPath is string cleaned)
                {
                    current = show = cleanedAudio = cleaned;
                    done.Add("clean audio");
                }
                else problem = result.Message ?? "The audio clean-up didn't finish.";
            }

            // MP4 live convert, continued: put the chapters (and cleaned sound) into the saved live MP4, or make
            // the MP4 from the AVI now if the live one wasn't usable.
            if (liveMp4 is { OutputPath: string savedMp4 })
            {
                // Cleaned sound comes from the AVI's timeline, so only use it when the live video lines up frame for frame.
                string? audioForMp4 = liveMp4.FrameExact ? cleanedAudio : null;
                if (chapters.Count > 0 || audioForMp4 is not null)
                {
                    ConvertProgressLabel.Text = "ADDING CHAPTERS";
                    var addProgress = StepProgress(ConvertProgressPanel, ConvertProgressBar, ConvertProgressText, ConvertTimeLeft, "Adding chapters to the MP4", Status);
                    var added = await Converter.AddToMp4Async(savedMp4, liveMp4.DurationSeconds, chapters, audioForMp4, addProgress, ct);
                    ConvertProgressPanel.Visibility = Visibility.Collapsed;
                    if (!added.Ok) problem = $"{added.Message} The MP4 is saved without them.";
                }
                done.Add("MP4 for Jellyfin");
            }
            else if (liveFinish is not null || liveWanted)
            {
                ConvertProgressLabel.Text = "MAKING MP4";
                var convertProgress = StepProgress(ConvertProgressPanel, ConvertProgressBar, ConvertProgressText, ConvertTimeLeft, "Making the MP4", Status);
                var mp4 = await Converter.ConvertAsync(current, Converter.Jellyfin, new ConvertOptions { Chapters = chapters }, convertProgress, ct);
                ConvertProgressPanel.Visibility = Visibility.Collapsed;
                if (mp4.Ok && mp4.OutputPath is string madeMp4)
                {
                    jellyfinMp4 = show = madeMp4;
                    TryDeleteFile(TransportController.LivePartPath(capturedPath)); // the unusable live copy
                    done.Add(liveProblem is null ? "MP4 for Jellyfin" : $"MP4 for Jellyfin (made after capture: {liveProblem.TrimEnd('.')})");
                }
                else problem = mp4.Message ?? "The MP4 couldn't be made.";
            }
            bool madeJellyfinMp4 = jellyfinMp4 is not null;

            // The Convert step would make the same Jellyfin MP4 again; skip it then. Other formats still run.
            bool sameAsLive = madeJellyfinMp4 && Converter.PresetById(_settings.ConvertPreset).Id == Converter.Jellyfin.Id;
            if (_settings.ConvertAfterCapture && !sameAsLive)
            {
                ConvertProgressLabel.Text = "CONVERTING";
                var convertProgress = StepProgress(ConvertProgressPanel, ConvertProgressBar, ConvertProgressText, ConvertTimeLeft, "Converting", Status);
                var result = await Converter.ConvertAsync(current, Converter.PresetById(_settings.ConvertPreset),
                    new ConvertOptions { Chapters = chapters }, convertProgress, ct);
                ConvertProgressPanel.Visibility = Visibility.Collapsed;
                if (result.Ok && result.OutputPath is string converted)
                {
                    show = converted;
                    done.Add($"{Path.GetExtension(converted).TrimStart('.').ToUpperInvariant()} copy");
                }
                else problem = result.Message ?? "The conversion didn't finish.";
            }

            bool topazUsesAvi = false;
            if (_settings.SendToTopaz)
            {
                if (PostProcess.OpenInTopaz(current))
                {
                    done.Add($"opened in Topaz (choose {_settings.TopazModel} there)");
                    topazUsesAvi = current == capturedPath;
                }
                else problem = "Couldn't open Topaz Video AI.";
            }

            // "Delete AVI after": only for a fresh capture, only when every step worked, and only once the MP4 has
            // been checked to be the whole tape. Anything doubtful keeps the AVI.
            if (fromCapture && liveWanted && DeleteAviOn && madeJellyfinMp4 && jellyfinMp4 is not null)
            {
                string? keepReason = problem is not null ? "a step above didn't finish"
                    : topazUsesAvi ? "Topaz Video AI has it open"
                    : await Mp4ShortfallAsync(capturedPath, jellyfinMp4, liveMp4 is not null ? captureDropped : 0, ct);
                if (keepReason is null)
                {
                    try { File.Delete(capturedPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { keepReason = $"it couldn't be deleted: {ex.Message}"; }
                }
                if (keepReason is null)
                {
                    TryDeleteFile(ChapterWriter.XmlPathFor(capturedPath)); // the chapters are inside the MP4
                    Log.Info($"Deleted {capturedPath}; kept {jellyfinMp4}");
                    done.Add("AVI deleted");
                }
                else
                {
                    Log.Warn($"Kept {capturedPath}: {keepReason}");
                    done.Add($"AVI kept ({keepReason})");
                }
            }
        }
        catch (OperationCanceledException)
        {
            problem = "Post-processing was stopped. The original file is untouched.";
            if (live is not null && liveFinish is not null && jellyfinMp4 is null && liveProblem is null)
            {
                // Never throw away a finished live MP4. The encoder only has a few seconds left to write when the
                // capture stops; let it finish, then keep the ".part" (a complete, playable MP4) as "Name.mp4".
                if (!liveFinish.IsCompleted)
                {
                    try { await liveFinish.WaitAsync(TimeSpan.FromSeconds(60)); } catch (Exception ex) { Log.Warn($"Live MP4 didn't finish: {ex.Message}"); }
                }
                if (liveFinish is { IsCompletedSuccessfully: true, Result.Ok: true })
                    jellyfinMp4 = Converter.KeepLivePart(TransportController.LivePartPath(capturedPath), capturedPath);
                else
                {
                    if (!liveFinish.IsCompleted) live.Abort();
                    TryDeleteFile(TransportController.LivePartPath(capturedPath));
                }
            }
            if (jellyfinMp4 is not null)
            {
                show = jellyfinMp4;
                problem = $"Post-processing was stopped. {Path.GetFileName(jellyfinMp4)} is saved and the original file is untouched.";
            }
            Log.Warn($"Post-processing stopped: {capturedPath}" + (jellyfinMp4 is not null ? $"; kept {jellyfinMp4}" : ""));
        }
        catch (Exception ex)
        {
            Log.Error("Post-processing", ex);
            problem = $"Post-processing hit an error: {ex.Message}";
        }
        finally
        {
            _postRunning = false;
            _liveMp4Saving = false;
            _postCts = null;
            cts.Dispose();
            ProcessFileButton.IsEnabled = true;
            ChapterProgressPanel.Visibility = ConvertProgressPanel.Visibility = Visibility.Collapsed;
            SetPostStatus(null);
        }

        string finished = done.Count > 0 ? string.Join(", ", done) : "";
        if (problem is not null)
            ShowToast(done.Count > 0 ? $"{problem} Finished before that: {finished}." : problem, ToastKind.Warning);
        else if (done.Count > 0)
            ShowToast($"{Path.GetFileName(capturedPath)}: {finished}.", ToastKind.Success, "Show in folder", () => ShowInExplorer(show));
    }

    /// <summary>
    /// Shows a step's progress bar in its card (scrolled into view), with the percentage and time left,
    /// and mirrors the percentage in the panel's status line. The caller hides the bar when the step ends.
    /// </summary>
    private Progress<double> StepProgress(FrameworkElement panel, ProgressBar bar, TextBlock percentText, TextBlock timeLeft,
        string verb, Action<string> status)
    {
        bar.Value = 0;
        percentText.Text = "0%";
        timeLeft.Text = "";
        timeLeft.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
        status($"{verb}… 0%");
        // Scroll the whole card (bar and the note under it) into view, not just the bar's top edge.
        var card = panel.Parent as FrameworkElement ?? panel;
        Dispatcher.InvokeAsync(() => card.BringIntoView(), DispatcherPriority.Loaded);

        var clock = Stopwatch.StartNew();
        return new Progress<double>(p =>
        {
            if (!_postRunning) return;
            bar.Value = p;
            string percent = $"{p * 100:0}%";
            percentText.Text = percent;
            if (TimeLeft(p, clock.Elapsed) is string left)
            {
                timeLeft.Text = char.ToUpperInvariant(left[0]) + left[1..];
                timeLeft.Visibility = Visibility.Visible;
            }
            status($"{verb}… {percent}");
        });
    }

    /// <summary>"about 6 min left", once there's enough progress to guess from.</summary>
    private static string? TimeLeft(double fraction, TimeSpan elapsed)
    {
        if (fraction < 0.02 || fraction >= 1 || elapsed < TimeSpan.FromSeconds(5)) return null;
        var left = TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - fraction) / fraction);
        if (left < TimeSpan.FromMinutes(1)) return "under a minute left";
        if (left < TimeSpan.FromHours(1)) return $"about {Math.Ceiling(left.TotalMinutes):0} min left";
        return $"about {(int)left.TotalHours} h {left.Minutes} min left";
    }

    /// <summary>Null when the MP4 is as long as the AVI (to within a second, or 0.2% of a long tape); otherwise why not.</summary>
    /// <remarks>
    /// Frames the capture dropped are filled in the AVI (so its timing stays right) but never reach the live MP4,
    /// which is shorter by exactly that much; <paramref name="captureDropped"/> allows for it.
    /// </remarks>
    private static async Task<string?> Mp4ShortfallAsync(string avi, string mp4, long captureDropped, CancellationToken ct)
    {
        MediaInfo? a = await FfmpegTools.ProbeAsync(avi, ct);
        MediaInfo? m = await FfmpegTools.ProbeAsync(mp4, ct);
        if (a is null || m is null || a.DurationSeconds <= 0) return "the MP4 couldn't be checked";
        double droppedSeconds = captureDropped / (a.FramesPerSecond > 0 ? a.FramesPerSecond : 29.97);
        double diff = a.DurationSeconds - droppedSeconds - m.DurationSeconds;
        Log.Info($"MP4 check before deleting the AVI: AVI {a.DurationSeconds:0.00} s ({droppedSeconds:0.00} s of it dropped frames), MP4 {m.DurationSeconds:0.00} s");
        return Math.Abs(diff) <= Math.Max(1.0, a.DurationSeconds * 0.002) ? null
            : $"the MP4 is {Math.Abs(diff):0} s {(diff > 0 ? "shorter" : "longer")} than it";
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log.Warn($"Couldn't delete {path}: {ex.Message}"); }
    }

    /// <summary>Shows what post-processing is doing at the top of the panel; null puts the description back.</summary>
    private void SetPostStatus(string? text)
    {
        PostIntro.Text = text ?? "Applied after capture completes.";
        if (text is null) PostIntro.ClearValue(TextBlock.ForegroundProperty);
        else PostIntro.Foreground = Res("B.Accent");
        CancelPostButton.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CancelPost_Click(object sender, RoutedEventArgs e) => _postCts?.Cancel();

    private async void ProcessFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.AddChapters && !_settings.ConvertAfterCapture && _settings.AudioPlugins.Count == 0 && !_settings.SendToTopaz)
        {
            ShowToast("Switch on at least one step above first, such as Chapters or Convert.", ToastKind.Warning);
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Choose a video to process",
            Filter = "Video files|*.avi;*.dv;*.mkv;*.mp4;*.mov;*.m4v;*.mpg;*.mpeg|All files|*.*",
            InitialDirectory = Directory.Exists(_settings.OutputFolder) ? _settings.OutputFolder : null,
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunPostProcessingAsync(dialog.FileName);
    }

    // ---------------------------------------------------------------- post processing panel

    private void UpdatePluginUi()
    {
        PluginEmpty.Visibility = _plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var available = PostProcess.AudioPlugins.Select(p => p.Name).Where(n => !_plugins.Contains(n)).ToList();
        if (_pluginChoice is null || !available.Contains(_pluginChoice)) _pluginChoice = available.FirstOrDefault();
        PluginChoiceText.Text = _pluginChoice ?? "All plugins added";
        PluginChoiceButton.IsEnabled = available.Count > 0;
        AddPluginButton.IsEnabled = _pluginChoice is not null;
    }

    private void SavePlugins()
    {
        _settings.AudioPlugins = _plugins.ToList();
        _settings.Save();
        UpdatePluginUi();
    }

    private void PluginChoice_Click(object sender, RoutedEventArgs e)
    {
        var available = PostProcess.AudioPlugins.Select(p => p.Name).Where(n => !_plugins.Contains(n));
        ShowMenu(PluginChoiceButton, available, choice =>
        {
            _pluginChoice = choice;
            UpdatePluginUi();
        });
    }

    private void AddPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (_pluginChoice is null || _plugins.Contains(_pluginChoice)) return;
        _plugins.Add(_pluginChoice);
        SavePlugins();
    }

    private void RemovePlugin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name && _plugins.Remove(name)) SavePlugins();
    }

    private void UpdateAiUi()
    {
        ModelText.Text = _settings.TopazModel;
        bool installed = _topazPath is not null;
        TopazName.Text = installed ? "Topaz Video AI" : "Topaz Video AI not found";
        ModelButton.IsEnabled = installed;
        AiSendButton.IsEnabled = installed;

        if (!installed)
        {
            AiCardEdge.ClearValue(Border.BorderBrushProperty);
            AiSendText.Text = "NOT INSTALLED";
            AiSendIcon.Data = Geo("Geo.Sparkle");
            AiNote.Text = "Install Topaz Video AI to send captures to it.";
            AiNote.Visibility = Visibility.Visible;
            return;
        }

        bool queued = _settings.SendToTopaz;
        if (queued)
        {
            AiCardEdge.BorderBrush = Res("B.AiQueuedEdge");
            AiSendButton.Background = (Brush)new BrushConverter().ConvertFrom("#0DFFFFFF")!;
            AiSendButton.Foreground = (Brush)new BrushConverter().ConvertFrom("#A6DDE8F6")!;
            AiSendIcon.Data = Geo("Geo.Check");
            AiSendText.Text = "WILL OPEN AFTER CAPTURE";
            AiNote.Text = $"Opens the finished file in Topaz. Choose {_settings.TopazModel} there.";
            AiNote.Visibility = Visibility.Visible;
        }
        else
        {
            AiCardEdge.ClearValue(Border.BorderBrushProperty);
            AiSendButton.ClearValue(BackgroundProperty);
            AiSendButton.ClearValue(ForegroundProperty);
            AiSendIcon.Data = Geo("Geo.Sparkle");
            AiSendText.Text = "SEND AFTER CAPTURE";
            AiNote.Visibility = Visibility.Collapsed;
        }
        AutomationProperties.SetName(AiSendButton, queued ? "Don't send to Topaz after capture" : "Send to Topaz after capture");
    }

    private void Model_Click(object sender, RoutedEventArgs e) =>
        ShowMenu(ModelButton, PostProcess.TopazModels, model =>
        {
            _settings.TopazModel = model;
            _settings.Save();
            UpdateAiUi();
        });

    private void AiSend_Click(object sender, RoutedEventArgs e)
    {
        _settings.SendToTopaz = !_settings.SendToTopaz;
        _settings.Save();
        UpdateAiUi();
    }

    // ---------------------------------------------------------------- preview sound

    private void UpdateMuteUi()
    {
        bool muted = _settings.MutePreview;
        MuteIcon.Data = Geo(muted ? "Geo.SpeakerOff" : "Geo.Speaker");
        MuteButton.ToolTip = muted
            ? "Sound muted on this PC (M). Captures still record it."
            : "Mute the sound on this PC (M). Captures still record it.";
        AutomationProperties.SetName(MuteButton, muted ? "Unmute playback sound" : "Mute playback sound");
        MuteButton.Foreground = muted ? Res("B.Yellow") : (Brush)new BrushConverter().ConvertFrom("#B3DEE8F6")!;
    }

    private async void Mute_Click(object sender, RoutedEventArgs e) => await ToggleMuteAsync();

    private async Task ToggleMuteAsync()
    {
        _settings.MutePreview = !_settings.MutePreview;
        _settings.Save();
        UpdateMuteUi();
        await _controller.SetPreviewMutedAsync(_settings.MutePreview);
    }

    // ---------------------------------------------------------------- MP4 live convert

    private bool LiveMp4On => _settings.LiveMp4 && _hasFfmpeg;

    private bool DeleteAviOn => LiveMp4On && _settings.DeleteAviAfterMp4;

    private string IdleStorageNote =>
        DeleteAviOn ? "MP4 for Jellyfin · AVI deleted once checked"
        : LiveMp4On ? "DV AVI untouched + MP4 for Jellyfin"
        : "DV AVI · No re-encoding";

    private void UpdateLiveMp4Ui()
    {
        bool on = LiveMp4On;
        LiveMp4Value.Text = _hasFfmpeg ? "" : "needs FFmpeg";
        SetSwitchLook(LiveMp4Button, LiveMp4Label, LiveMp4Track, LiveMp4Knob, on);
        LiveMp4Button.IsEnabled = _hasFfmpeg && !_capturing;
        LiveMp4Button.ToolTip = _hasFfmpeg
            ? "Makes an MP4 that plays in Jellyfin on any phone or browser while the tape plays, next to the untouched AVI."
            : FfmpegTools.MissingMessage;
        AutomationProperties.SetName(LiveMp4Button, on ? "MP4 live convert, on" : "MP4 live convert, off");

        // Deleting the AVI only makes sense when there's a checked MP4 to keep instead.
        bool delete = DeleteAviOn;
        DeleteAviButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SetSwitchLook(DeleteAviButton, DeleteAviLabel, DeleteAviTrack, DeleteAviKnob, delete, warn: true);
        DeleteAviButton.IsEnabled = !_capturing;
        DeleteAviButton.ToolTip = "Deletes the AVI for good once the MP4 has been checked to be the same length. " +
            "The AVI is the exact copy of the tape; without it, a better copy later means playing the tape again.";
        AutomationProperties.SetName(DeleteAviButton, delete ? "Delete the AVI after the MP4 is checked, on" : "Delete the AVI after the MP4 is checked, off");

        FileExtText.Text = delete ? ".MP4" : on ? ".AVI + .MP4" : ".AVI";
        if (!_capturing) StorageNote.Text = IdleStorageNote;
        UpdateFreeSpace();
    }

    /// <summary>The iOS-style switch: knob right and lit when on (amber for a switch that deletes things).</summary>
    private void SetSwitchLook(Button row, TrackedText label, Border track, FrameworkElement knob, bool on, bool warn = false)
    {
        label.Foreground = on ? Res(warn ? "B.Yellow" : "B.Accent") : Res("B.Label");
        if (on && !warn) row.BorderBrush = Res("B.AiQueuedEdge");
        else row.ClearValue(BorderBrushProperty); // back to the style, which lights the edge on hover
        knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        track.Background = !on ? (Brush)new BrushConverter().ConvertFrom("#1FFFFFFF")!
            : warn ? new LinearGradientBrush(Color.FromRgb(0xE0, 0x9A, 0x3A), Color.FromRgb(0xF2, 0xC2, 0x6B), 0)
            : new LinearGradientBrush(Color.FromRgb(0x4C, 0x8D, 0xFF), Color.FromRgb(0x7F, 0xC4, 0xFF), 0);
        track.BorderBrush = (Brush)new BrushConverter().ConvertFrom(!on ? "#26FFFFFF" : warn ? "#66F2C26B" : "#668FC8FF")!;
    }

    private void DeleteAvi_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing) return;
        _settings.DeleteAviAfterMp4 = !_settings.DeleteAviAfterMp4;
        _settings.Save();
        UpdateLiveMp4Ui();
    }

    private void LiveMp4_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing) return;
        _settings.LiveMp4 = !_settings.LiveMp4;
        _settings.Save();
        UpdateLiveMp4Ui();
        UpdateConvertUi(); // its notes mention the live MP4
    }

    private void UpdateChapterUi()
    {
        bool on = _settings.AddChapters && _hasFfmpeg;
        SetQueuedLook(ChapterCardEdge, ChapterButton, ChapterIcon, ChapterText, on,
            _hasFfmpeg ? "ADD AFTER CAPTURE" : "FFMPEG NOT INSTALLED", "WILL ADD AFTER CAPTURE", "Geo.Chapters");
        ChapterButton.IsEnabled = _hasFfmpeg;
        ChapterNote.Text = !_hasFfmpeg ? FfmpegTools.MissingMessage
            : !on ? "Off. Captures are saved without chapters."
            : LiveMp4On
                ? "Scans the finished capture (about a minute per tape). The chapters go inside the MP4, where Jellyfin shows them."
            : _settings.ConvertAfterCapture
                ? "Scans the finished capture (about a minute per tape). The chapters go inside the converted copy, where Jellyfin shows them."
                : "Scans the finished capture (about a minute per tape). AVI files can't hold chapters, so Jellyfin won't see them: turn on Convert to put them inside a copy.";
        AutomationProperties.SetName(ChapterButton, on ? "Don't add chapters after capture" : "Add chapters after capture");
    }

    private void ChapterToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.AddChapters = !_settings.AddChapters;
        _settings.Save();
        UpdateChapterUi();
    }

    private void UpdateConvertUi()
    {
        ConvertPreset preset = Converter.PresetById(_settings.ConvertPreset);
        PresetText.Text = preset.Name;
        bool on = _settings.ConvertAfterCapture && _hasFfmpeg;
        SetQueuedLook(ConvertCardEdge, ConvertButton, ConvertIcon, ConvertText, on,
            _hasFfmpeg ? "CONVERT AFTER CAPTURE" : "FFMPEG NOT INSTALLED", "WILL CONVERT AFTER CAPTURE", "Geo.Film");
        ConvertButton.IsEnabled = PresetButton.IsEnabled = _hasFfmpeg;
        ConvertNote.Text = !_hasFfmpeg ? FfmpegTools.MissingMessage
            : on && LiveMp4On && preset.Id == Converter.Jellyfin.Id
                ? "MP4 live convert (02) already makes this copy during capture, so this step is skipped. Choose another format to get a second copy."
            : on ? $"{preset.Description} The capture itself is kept."
            : preset.Description;
        AutomationProperties.SetName(ConvertButton, on ? "Don't convert after capture" : "Convert after capture");
        UpdateChapterUi(); // its note depends on whether a converted copy will carry the chapters
    }

    private void ConvertToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ConvertAfterCapture = !_settings.ConvertAfterCapture;
        _settings.Save();
        UpdateConvertUi();
    }

    private void Preset_Click(object sender, RoutedEventArgs e) =>
        ShowMenu(PresetButton, Converter.Presets.Select(p => p.Name), name =>
        {
            _settings.ConvertPreset = Converter.Presets.First(p => p.Name == name).Id;
            _settings.Save();
            UpdateConvertUi();
        });

    /// <summary>The "switched on" look shared by the post-processing cards: lit edge, quiet button, a tick.</summary>
    private void SetQueuedLook(Border card, Button button, Icon icon, TrackedText label, bool on, string offText, string onText, string offIcon)
    {
        if (on)
        {
            card.BorderBrush = Res("B.AiQueuedEdge");
            button.Background = (Brush)new BrushConverter().ConvertFrom("#0DFFFFFF")!;
            button.Foreground = (Brush)new BrushConverter().ConvertFrom("#A6DDE8F6")!;
            icon.Data = Geo("Geo.Check");
            label.Text = onText;
        }
        else
        {
            card.ClearValue(Border.BorderBrushProperty);
            button.ClearValue(BackgroundProperty);
            button.ClearValue(ForegroundProperty);
            icon.Data = Geo(offIcon);
            label.Text = offText;
        }
    }

    /// <summary>A small dark menu under <paramref name="anchor"/>; picks call <paramref name="pick"/>.</summary>
    private void ShowMenu(FrameworkElement anchor, IEnumerable<string> items, Action<string> pick)
    {
        var panel = new StackPanel();
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
        };
        foreach (string item in items)
        {
            var button = new Button { Content = item, Style = (Style)FindResource("MenuButton") };
            button.Click += (_, _) =>
            {
                popup.IsOpen = false;
                pick(item);
            };
            panel.Children.Add(button);
        }
        if (panel.Children.Count == 0) return;

        var border = new Border
        {
            MinWidth = Math.Max(anchor.ActualWidth, 200),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Background = Res("B.Menu"),
            BorderBrush = Res("B.CardEdge"),
            Child = panel,
        };
        // Code-made popups aren't in the window's tree, so set the inherited text properties here.
        TextElement.SetFontFamily(border, (FontFamily)FindResource("F.Ui"));
        TextElement.SetFontSize(border, 14);
        TextElement.SetForeground(border, Res("B.Text"));
        popup.Child = border;
        popup.IsOpen = true;
        (panel.Children[0] as Button)?.Focus();
    }

    // ---------------------------------------------------------------- save location

    private void UpdateFolderUi()
    {
        string folder = _settings.OutputFolder;
        string trimmed = folder.TrimEnd('\\');
        string name = Path.GetFileName(trimmed);
        FolderName.Text = string.IsNullOrEmpty(name) ? trimmed : name;
        FolderButton.ToolTip = $"Saving to {folder}";
    }

    private void UpdateFreeSpace()
    {
        try
        {
            string root = Path.GetPathRoot(_settings.OutputFolder) ?? throw new IOException();
            var drive = new DriveInfo(root);
            if (!drive.IsReady) throw new IOException();
            long free = drive.AvailableFreeSpace, total = drive.TotalSize;

            FreeLabel.Text = $"FREE SPACE ON {root.TrimEnd('\\').ToUpperInvariant()}";
            FreeText.Text = FormatBytes(free);
            UsedColumn.Width = new GridLength(Math.Max(0.001, total - free), GridUnitType.Star);
            FreeColumn.Width = new GridLength(Math.Max(0.001, free), GridUnitType.Star);

            double hours = free / (AppSettings.DvBytesPerHour + (LiveMp4On ? AppSettings.LiveMp4BytesPerHour : 0));
            bool low = free < AppSettings.MinimumFreeBytes;
            HoursText.Text = low ? "NOT ENOUGH ROOM"
                : hours >= 2 ? $"≈ {Math.Floor(hours):0} HOURS"
                : hours >= 1 ? "≈ 1 HOUR"
                : $"≈ {Math.Floor(hours * 60):0} MINUTES";
            HoursText.Foreground = low ? Res("B.Yellow") : Res("B.Label");
        }
        catch
        {
            FreeLabel.Text = "FREE SPACE";
            FreeText.Text = "—";
            UsedColumn.Width = new GridLength(0, GridUnitType.Star);
            FreeColumn.Width = new GridLength(1, GridUnitType.Star);
            HoursText.Text = "DRIVE NOT AVAILABLE";
            HoursText.Foreground = Res("B.Yellow");
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        FolderMenu.IsOpen = !FolderMenu.IsOpen;
        if (FolderMenu.IsOpen) ChooseFolderItem.Focus();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        FolderMenu.IsOpen = false;
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to save captures",
            InitialDirectory = Directory.Exists(_settings.OutputFolder) ? _settings.OutputFolder : null,
        };
        if (dialog.ShowDialog(this) != true) return;

        if (AppSettings.ValidateFolder(dialog.FolderName) is string problem)
        {
            ShowToast(problem, ToastKind.Warning);
            return;
        }
        _settings.OutputFolder = dialog.FolderName;
        _settings.Save();
        UpdateFolderUi();
        UpdateFreeSpace();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        FolderMenu.IsOpen = false;
        try
        {
            Directory.CreateDirectory(_settings.OutputFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.OutputFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast($"Couldn't open {_settings.OutputFolder}. {ex.Message}", ToastKind.Error);
        }
    }

    private static void ShowInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Show in folder", ex); }
    }

    private void FileName_TextChanged(object sender, TextChangedEventArgs e)
    {
        _settings.FileName = FileNameBox.Text;
        SummaryName.Text = string.IsNullOrWhiteSpace(FileNameBox.Text) ? "—" : FileNameBox.Text;
    }

    private void FileName_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        string cleaned = AppSettings.CleanFileName(FileNameBox.Text);
        if (cleaned != FileNameBox.Text) FileNameBox.Text = cleaned;
        _settings.FileName = cleaned;
        _settings.Save();
    }

    // ---------------------------------------------------------------- toast

    private void ShowToast(string text, ToastKind kind, string? actionLabel = null, Action? action = null)
    {
        ToastText.Text = text;
        (string geo, string brush) = kind switch
        {
            ToastKind.Success => ("Geo.CheckCircle", "B.Accent"),
            ToastKind.Warning => ("Geo.Warning", "B.Yellow"),
            _ => ("Geo.ErrorCircle", "B.Red"),
        };
        ToastIcon.Data = Geo(geo);
        ToastIcon.Foreground = Res(brush);
        _toastAction = action;
        ToastAction.Content = actionLabel;
        ToastAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        Toast.Visibility = Visibility.Visible;

        // Errors stay until dismissed; everything else gets out of the way.
        _toastTimer.Stop();
        if (kind != ToastKind.Error)
        {
            _toastTimer.Interval = TimeSpan.FromSeconds(kind == ToastKind.Success ? 12 : 10);
            _toastTimer.Start();
        }
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.Visibility = Visibility.Collapsed;
        _toastAction = null;
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _toastAction;
        HideToast();
        action?.Invoke();
    }

    private void ToastClose_Click(object sender, RoutedEventArgs e) => HideToast();

    // ---------------------------------------------------------------- window

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged()
    {
        bool maximized = WindowState == WindowState.Maximized;
        // A maximised custom-chrome window overhangs the screen edge; pull the content back in.
        Root.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeIcon.Data = Geo(maximized ? "Geo.Restore" : "Geo.Maximize");
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximise";
        AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximise");
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat || Keyboard.FocusedElement is TextBox) return;
        // Space/Enter on a focused non-transport button (Check again, menus…) should press that button.
        if (Keyboard.FocusedElement is ButtonBase focused && !_transportButtons.Contains(focused) && e.Key is Key.Space or Key.Enter)
            return;

        if (e.Key == Key.M) // mute works while capturing too
        {
            e.Handled = true;
            await ToggleMuteAsync();
            return;
        }

        if (e.Key == Key.R)
        {
            if (CaptureButton.IsEnabled)
            {
                e.Handled = true;
                Capture_Click(CaptureButton, new RoutedEventArgs());
            }
            return;
        }

        if (!_connected || _capturing) return;
        TransportCommand? command = e.Key switch
        {
            Key.Space => _mode == TransportMode.Playing ? TransportCommand.Pause : TransportCommand.Play,
            Key.J => TransportCommand.Rewind,
            Key.K => TransportCommand.Stop,
            Key.L => TransportCommand.FastForward,
            _ => null,
        };
        if (command is null) return;
        e.Handled = true;
        await SendAsync(command.Value);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_capturing)
        {
            var answer = MessageBox.Show(this, "A capture is still running. Stop it, save the file, and close?",
                "Tape Time Machine", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        if (_postRunning)
        {
            // The live MP4 is only safe once it's saved; closing before that can lose it, so say so plainly.
            var answer = _liveMp4Saving
                ? MessageBox.Show(this, "This tape's MP4 is still being saved. It takes a minute or two.\n\n" +
                    "Close anyway? The MP4 may be lost (the AVI is kept). Choose No to wait.",
                    "Tape Time Machine", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                : MessageBox.Show(this, "Post-processing is still running. Stop it and close?" +
                    (_settings.LiveMp4 ? "\n\nAny MP4 already saved is kept." : ""),
                    "Tape Time Machine", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _postCts?.Cancel();
        }
        _settings.FileName = AppSettings.CleanFileName(FileNameBox.Text);
        _settings.Save();
        _controller.Dispose(); // stops any capture cleanly so the AVI is finalised
    }

    // ---------------------------------------------------------------- formatting

    private static long? FreeBytes(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace; } catch { return null; }
    }

    private static string FormatClock(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>Sizes the way File Explorer shows them (1 GB = 1024³ bytes).</summary>
    private static string FormatBytes(long bytes)
    {
        const double TB = 1024d * 1024 * 1024 * 1024, GB = 1024d * 1024 * 1024, MB = 1024d * 1024;
        if (bytes >= 1000 * GB) return $"{bytes / TB:0.00} TB"; // "1.44 TB" fits where "1476.3 GB" doesn't
        if (bytes >= GB) return $"{bytes / GB:0.0} GB";
        return $"{bytes / MB:0} MB";
    }
}
