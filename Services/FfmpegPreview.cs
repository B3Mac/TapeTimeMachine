using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DirectShowLib;

namespace TapeRescue.Services;

/// <summary>
/// Live preview decoder. Sits on the Smart Tee's preview branch as a Sample Grabber callback, copies each
/// raw DV frame the moment it arrives, and decodes the copies with FFmpeg on other threads.
///
/// Why: decoding straight from the camcorder driver's buffers produced 8x8 blocks wherever the picture moved
/// (the driver reuses its few buffers while the decoder is still reading), although the same DV decoded
/// cleanly from the capture file. Copying first removes the race; FFmpeg also deinterlaces properly (bwdif).
/// The capture branch is untouched. Latency is about three frames.
/// </summary>
[ComVisible(true)]
public sealed class FfmpegPreview : ISampleGrabberCB, IDisposable
{
    private readonly FrameSink _sink;
    private readonly string _ffmpeg;
    private readonly object _gate = new();
    // Keep only the newest few frames: if FFmpeg falls behind, drop old preview frames rather than lag.
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    private Process? _process;
    private int _dvFrameBytes;
    private volatile bool _stopped;

    public FfmpegPreview(FrameSink sink, string ffmpegPath)
    {
        _sink = sink;
        _ffmpeg = ffmpegPath;
    }

    public int SampleCB(double sampleTime, IMediaSample sample) => 0;

    public int BufferCB(double sampleTime, IntPtr buffer, int length)
    {
        if (_stopped || length < 100_000) return 0;
        try
        {
            // Copy now, before the driver reuses this buffer.
            var copy = new byte[length];
            Marshal.Copy(buffer, copy, 0, length);
            if (_process is null) Start(length);
            _queue.Writer.TryWrite(copy);
        }
        catch (Exception ex)
        {
            Log.Error("FFmpeg preview input", ex);
        }
        return 0;
    }

    private void Start(int dvFrameBytes)
    {
        lock (_gate)
        {
            if (_process is not null || _stopped) return;
            _dvFrameBytes = dvFrameBytes;
            // 144,000-byte DV frames are PAL (720x576); 120,000 are NTSC (720x480).
            int width = 720, height = dvFrameBytes >= 144_000 ? 576 : 480;
            _sink.Configure(width, height, bottomUp: false);

            var psi = new ProcessStartInfo(_ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[]
                     {
                         "-hide_banner", "-loglevel", "error",
                         "-f", "dv", "-probesize", "32", "-analyzeduration", "0", "-fflags", "nobuffer", "-flags", "low_delay",
                         "-i", "pipe:0", "-an",
                         "-vf", "bwdif=mode=send_frame",
                         "-pix_fmt", "bgra", "-f", "rawvideo", "-flush_packets", "1", "pipe:1",
                     })
                psi.ArgumentList.Add(a);

            _process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg didn't start.");
            Log.Info($"FFmpeg preview started ({width}x{height}): {_ffmpeg}");

            var process = _process;
            _ = Task.Run(() => WriteLoop(process));
            _ = Task.Run(() => ReadLoop(process, width * height * 4));
            _ = Task.Run(async () =>
            {
                // Tape spin-up and dropouts make FFmpeg report (and conceal) DV bitstream errors, sometimes
                // dozens per second. Log a few, then just count them.
                string? line;
                int count = 0;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    if (++count <= 5) Log.Warn($"FFmpeg preview: {line}");
                    else if (count % 1000 == 0) Log.Warn($"FFmpeg preview: {count} messages so far (DV errors from the tape are concealed)");
                }
            });
        }
    }

    private async Task WriteLoop(Process process)
    {
        try
        {
            var stdin = process.StandardInput.BaseStream;
            await foreach (var frame in _queue.Reader.ReadAllAsync())
            {
                await stdin.WriteAsync(frame);
                await stdin.FlushAsync();
            }
            stdin.Close();
        }
        catch (Exception ex) when (_stopped || ex is IOException or ObjectDisposedException)
        {
            if (!_stopped) Log.Warn($"FFmpeg preview input closed: {ex.Message}");
        }
    }

    private void ReadLoop(Process process, int frameBytes)
    {
        try
        {
            var stdout = process.StandardOutput.BaseStream;
            var frame = new byte[frameBytes];
            while (!_stopped)
            {
                int read = 0;
                while (read < frameBytes)
                {
                    int n = stdout.Read(frame, read, frameBytes - read);
                    if (n <= 0) return;
                    read += n;
                }
                _sink.PushTopDown(frame);
            }
        }
        catch (Exception ex) when (_stopped || ex is IOException or ObjectDisposedException)
        {
            if (!_stopped) Log.Warn($"FFmpeg preview output closed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _queue.Writer.TryComplete();
        lock (_gate)
        {
            if (_process is null) return;
            try { if (!_process.HasExited) _process.Kill(); } catch { }
            _process.Dispose();
            _process = null;
        }
    }
}
