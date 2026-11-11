using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace TapeRescue.Services;

/// <summary>What the live encoder produced, for the post-capture check.</summary>
/// <param name="FramesIn">DV frames handed to FFmpeg.</param>
/// <param name="Dropped">Frames lost because FFmpeg fell too far behind (the MP4 then isn't used).</param>
public sealed record LiveMp4Result(bool Ok, string? PartPath, long FramesIn, long Dropped, string? Message = null);

/// <summary>
/// Makes the Jellyfin/phone MP4 while the tape plays, from the same copied DV frames the preview uses
/// (each DV frame carries its own sound). The AVI capture is separate and untouched.
///
/// The settings match the "Jellyfin and web (MP4)" conversion: each field becomes a frame (59.94 fps for NTSC),
/// square pixels (640×480), H.264 High CRF 18 and AAC 256 kbit/s. Those play directly in Jellyfin's web app on
/// iPhone, Android and desktop browsers, so nobody's phone needs the server to convert.
///
/// It writes a fragmented MP4 ("Name.mp4.part"), which stays readable even if the app or PC stops mid-tape and
/// which Jellyfin ignores. After the capture it is copied (not re-encoded) into the final "Name.mp4" with
/// chapters, laid out for quick start-up when streaming.
/// </summary>
public sealed class LiveMp4Encoder : IDisposable
{
    /// <summary>About 30 seconds of DV (~108 MB). If FFmpeg is ever that far behind, frames are dropped and counted.</summary>
    private const int QueueFrames = 900;

    public static readonly string[] VideoArgs =
    [
        "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-maxrate", "12M", "-bufsize", "24M",
        "-profile:v", "high", "-pix_fmt", "yuv420p",
    ];

    /// <summary>Tape-to-web picture: one frame per field, then the true 4:3 (or 16:9) shape in square pixels.</summary>
    public const string VideoFilter = "bwdif=mode=send_field,scale=w='trunc(ih*dar/2)*2':h=ih:flags=lanczos,setsar=1";

    public static readonly string[] AudioArgs = ["-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-ac", "2"];

    /// <summary>Standard-definition colour tags, so players don't assume HD colour.</summary>
    public static string[] ColourArgs(bool pal) => pal
        ? ["-color_primaries", "bt470bg", "-color_trc", "gamma28", "-colorspace", "bt470bg"]
        : ["-color_primaries", "smpte170m", "-color_trc", "smpte170m", "-colorspace", "smpte170m"];

    private readonly string _ffmpeg;
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(QueueFrames) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly object _gate = new();
    private readonly StringBuilder _errors = new();
    private Process? _process;
    private Task? _writeLoop, _errorLoop;
    private long _framesIn, _dropped;
    private volatile bool _finished;

    public string PartPath { get; }

    /// <summary>Frames lost so far because FFmpeg fell behind. Shown as a warning while capturing.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public LiveMp4Encoder(string ffmpegPath, string partPath)
    {
        _ffmpeg = ffmpegPath;
        PartPath = partPath;
    }

    /// <summary>Called on DirectShow's streaming thread with a private copy of one DV frame. Never blocks.</summary>
    public void Push(byte[] dvFrame)
    {
        if (_finished) return;
        try
        {
            if (_process is null) Start(dvFrame.Length);
            if (_queue.Writer.TryWrite(dvFrame)) Interlocked.Increment(ref _framesIn);
            else if (Interlocked.Increment(ref _dropped) is 1 or 100 or 1000)
                Log.Warn($"Live MP4 is behind; {Dropped} frames dropped");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _dropped);
            Log.Error("Live MP4 input", ex);
        }
    }

    private void Start(int dvFrameBytes)
    {
        lock (_gate)
        {
            if (_process is not null || _finished) return;
            bool pal = dvFrameBytes >= 144_000; // 144,000-byte frames are PAL, 120,000 NTSC

            var psi = FfmpegTools.NewProcess(_ffmpeg);
            psi.RedirectStandardInput = true;
            var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-f", "dv", "-i", "pipe:0",
                "-map", "0:v:0", "-map", "0:a:0?", "-vf", VideoFilter };
            args.AddRange(VideoArgs);
            args.AddRange(ColourArgs(pal));
            args.AddRange(AudioArgs);
            // Fragmented: every couple of seconds is complete on disk, so a crash leaves a playable file.
            args.AddRange(["-f", "mp4", "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "2000000", PartPath]);
            foreach (var a in args) psi.ArgumentList.Add(a);

            _process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg didn't start.");
            FfmpegTools.LowerPriority(_process); // the AVI capture always comes first
            Log.Info($"Live MP4 started ({(pal ? "PAL" : "NTSC")}): {PartPath}");

            var process = _process;
            _writeLoop = Task.Run(() => WriteLoop(process));
            _errorLoop = Task.Run(async () =>
            {
                string? line;
                int count = 0;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    // DV dropouts on old tape make FFmpeg report concealed errors; keep a few for the log.
                    if (++count <= 20) lock (_errors) _errors.AppendLine(line);
                }
            });
            _ = Task.Run(async () =>
            {
                try { await process.StandardOutput.ReadToEndAsync(); } catch { }
            });
        }
    }

    private async Task WriteLoop(Process process)
    {
        try
        {
            var stdin = process.StandardInput.BaseStream;
            await foreach (var frame in _queue.Reader.ReadAllAsync())
                await stdin.WriteAsync(frame);
            await stdin.FlushAsync();
            stdin.Close(); // end of input: FFmpeg finishes the file
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Warn($"Live MP4 input closed early: {ex.Message}");
            // Keep draining so Push never sees a full queue because of a dead encoder.
            while (_queue.Reader.TryRead(out _)) Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Ends the input and waits for FFmpeg to finish writing. Call after the capture has stopped.</summary>
    public async Task<LiveMp4Result> FinishAsync(CancellationToken ct = default)
    {
        _finished = true;
        _queue.Writer.TryComplete();
        Process? process;
        lock (_gate) process = _process;
        if (process is null) return new(false, null, 0, 0, "No picture reached the live MP4.");

        try
        {
            if (_writeLoop is not null) await _writeLoop.WaitAsync(TimeSpan.FromMinutes(5), ct);
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(5), ct);
            if (_errorLoop is not null) await _errorLoop.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (TimeoutException)
        {
            FfmpegTools.TryKill(process);
            return new(false, PartPath, Interlocked.Read(ref _framesIn), Dropped, "The live MP4 didn't finish in time.");
        }

        long frames = Interlocked.Read(ref _framesIn);
        Log.Info($"Live MP4 finished: exit {process.ExitCode}, {frames} frames in, {Dropped} dropped");
        string errors;
        lock (_errors) errors = _errors.ToString();
        if (errors.Length > 0) Log.Warn($"Live MP4 FFmpeg messages: {errors}");

        bool ok = process.ExitCode == 0 && File.Exists(PartPath) && new FileInfo(PartPath).Length > 0;
        return new(ok, PartPath, frames, Dropped, ok ? null : $"FFmpeg stopped with code {process.ExitCode}.");
    }

    /// <summary>Stops without keeping anything (for a capture that recorded nothing).</summary>
    public void Abort()
    {
        _finished = true;
        _queue.Writer.TryComplete();
        lock (_gate)
        {
            if (_process is not null)
            {
                FfmpegTools.TryKill(_process);
                try { _process.WaitForExit(5000); } catch { }
            }
        }
        try { if (File.Exists(PartPath)) File.Delete(PartPath); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _process?.Dispose();
            _process = null;
        }
    }
}
