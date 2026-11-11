#if DEBUG
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TapeRescue.Services;

namespace TapeRescue;

/// <summary>
/// Debug-only design preview: run with --demo, --demo-capturing or --demo-missing to see the UI
/// with a generated test picture and tone, without opening the camcorder. Never in Release builds.
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer? _demoTimer;

    public static bool DemoRequested => Environment.GetCommandLineArgs().Any(a => a.StartsWith("--demo", StringComparison.OrdinalIgnoreCase));
    // --demo-post also scrolls the post-processing panel to its end.
    private static bool DemoArg(string name) => Environment.GetCommandLineArgs().Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void StartDemo()
    {
        if (DemoArg("--demo-missing"))
        {
            OnConnectionChanged(new ConnectionInfo(ConnectionState.NoCamcorder));
            return;
        }

        const int w = 720, h = 480;
        byte[] frame = DemoFrame(w, h);
        _controller.Frames.Configure(w, h, bottomUp: true);
        _controller.Audio.Configure(48000, 2, 16);
        var frameHandle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        var tone = new short[1600];
        var toneHandle = GCHandle.Alloc(tone, GCHandleType.Pinned);
        Closed += (_, _) =>
        {
            _demoTimer?.Stop();
            frameHandle.Free();
            toneHandle.Free();
        };

        OnConnectionChanged(new ConnectionInfo(ConnectionState.Connected, "Demo camcorder",
            PreviewAvailable: true, CaptureAvailable: true));
        OnSnapshot(new TransportSnapshot(TransportMode.Playing, 0, new Models.TapeTime(0, 18, 24, 12), null, true));

        var started = DateTime.Now;
        var random = new Random(7);
        if (DemoArg("--demo-capturing")) SetCapturing(true);
        if (DemoArg("--demo-post")) Dispatcher.InvokeAsync(() => PostScroll.ScrollToEnd(), DispatcherPriority.Loaded);

        const string processArg = "--demo-process=";
        if (Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith(processArg, StringComparison.OrdinalIgnoreCase)) is string process)
            _ = DemoProcessAsync(process[processArg.Length..]);
        _demoTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            _controller.Frames.BufferCB(0, frameHandle.AddrOfPinnedObject(), frame.Length);

            // A wandering level so the meter moves like real speech or music.
            short amplitude = (short)(9000 + random.Next(0, 16000));
            for (int i = 0; i < tone.Length; i++) tone[i] = (short)(Math.Sin(i * 0.07) * amplitude);
            _controller.Audio.BufferCB(0, toneHandle.AddrOfPinnedObject(), tone.Length * 2);

            if (_capturing)
            {
                var elapsed = DateTime.Now - started + TimeSpan.FromMinutes(12);
                OnCaptureProgress(new CaptureProgress(elapsed, (long)(elapsed.TotalSeconds * 3_600_000), 0));
            }
        }, Dispatcher);
    }

    /// <summary>--demo-process=FILE: runs post-processing on a file, with conversion on for this run only.</summary>
    private async Task DemoProcessAsync(string file)
    {
        bool before = _settings.ConvertAfterCapture;
        _settings.ConvertAfterCapture = true;
        UpdateConvertUi();
        try { await RunPostProcessingAsync(file); }
        finally
        {
            _settings.ConvertAfterCapture = before;
            UpdateConvertUi();
        }
    }

    /// <summary>A soft sunset-over-water test card, bottom-up BGRA like a DirectShow DIB.</summary>
    private static byte[] DemoFrame(int w, int h)
    {
        var px = new byte[w * h * 4];
        (double r, double g, double b)[] sky = [(233, 147, 90), (216, 120, 79), (166, 90, 84), (91, 74, 99)];
        for (int y = 0; y < h; y++)
        {
            double v = y / (double)h;
            for (int x = 0; x < w; x++)
            {
                double u = x / (double)w;
                (double r, double g, double b) c;
                if (v < 0.62)
                {
                    double t = v / 0.62 * 3;
                    int i = Math.Min(2, (int)t);
                    double f = t - i;
                    c = (sky[i].r + (sky[i + 1].r - sky[i].r) * f, sky[i].g + (sky[i + 1].g - sky[i].g) * f, sky[i].b + (sky[i + 1].b - sky[i].b) * f);
                }
                else
                {
                    double f = (v - 0.62) / 0.38;
                    c = (45 - 16 * f, 79 - 21 * f, 92 - 23 * f);
                }
                double dx = (u - 0.68) / 0.18, dy = (v - 0.38) / 0.24;
                double sun = Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy));
                c = (c.r + (255 - c.r) * sun * 0.9, c.g + (236 - c.g) * sun * 0.9, c.b + (190 - c.b) * sun * 0.9);
                int row = h - 1 - y; // bottom-up
                int o = (row * w + x) * 4;
                px[o] = (byte)Math.Clamp(c.b, 0, 255);
                px[o + 1] = (byte)Math.Clamp(c.g, 0, 255);
                px[o + 2] = (byte)Math.Clamp(c.r, 0, 255);
                px[o + 3] = 255;
            }
        }
        return px;
    }
}
#endif
