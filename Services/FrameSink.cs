using System.Runtime.InteropServices;
using DirectShowLib;

namespace TapeRescue.Services;

/// <summary>
/// Receives decoded RGB32 preview frames from the Sample Grabber (on DirectShow's streaming thread)
/// and keeps three top-down copies for the UI:
///  - the full frame for the preview,
///  - a small "glass" copy (1/8 size) that the floating controls draw behind themselves as a soft blur,
///  - a tiny "ambient" copy that lights the window background.
/// </summary>
[ComVisible(true)]
public sealed class FrameSink : ISampleGrabberCB
{
    public const int GlassScale = 8;
    public const int AmbientScale = 30;

    private readonly object _gate = new();
    private int _width, _height;
    private bool _bottomUp;
    private byte[]? _full, _glass, _ambient;
    private bool _fresh, _ambientFresh;
    private long _lastFrameTicks, _lastPictureTicks;
    private int _frameCount;

    /// <summary>
    /// Brightness spread (standard deviation, 0..255) below which a frame counts as blank: one flat colour, such as
    /// the grey, blue or black a camcorder shows for unrecorded tape. Measured on the 1/8-size copy. On real Digital8
    /// captures, blank tape measured exactly 0.0 on every frame, while the darkest real picture sampled was 4.4.
    /// </summary>
    public const double BlankSpread = 1.0;

    /// <summary>Environment.TickCount64 when the last frame with a real picture arrived (0 if none yet).</summary>
    public long LastPictureTicks => Interlocked.Read(ref _lastPictureTicks);

    /// <summary>Environment.TickCount64 when the last frame of any kind arrived (0 if none yet).</summary>
    public long LastFrameTicks => Interlocked.Read(ref _lastFrameTicks);

    public int Width { get { lock (_gate) return _width; } }
    public int Height { get { lock (_gate) return _height; } }

    /// <summary>Milliseconds since the last frame arrived (very large if none yet).</summary>
    public long MillisecondsSinceLastFrame
    {
        get
        {
            long last = Interlocked.Read(ref _lastFrameTicks);
            return last == 0 ? long.MaxValue : Environment.TickCount64 - last;
        }
    }

    public void Configure(int width, int height, bool bottomUp)
    {
        lock (_gate)
        {
            _width = width;
            _height = height;
            _bottomUp = bottomUp;
            _full = new byte[width * height * 4];
            _glass = new byte[(width / GlassScale) * (height / GlassScale) * 4];
            _ambient = new byte[Math.Max(1, width / AmbientScale) * Math.Max(1, height / AmbientScale) * 4];
            _fresh = _ambientFresh = false;
        }
        Interlocked.Exchange(ref _lastFrameTicks, 0);
        Interlocked.Exchange(ref _lastPictureTicks, 0);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _lastFrameTicks, 0);
        Interlocked.Exchange(ref _lastPictureTicks, 0);
    }

    public int SampleCB(double sampleTime, IMediaSample sample) => 0;

    public int BufferCB(double sampleTime, IntPtr buffer, int length)
    {
        try
        {
            lock (_gate)
            {
                if (_full is null || _width == 0) return 0;
                int stride = _width * 4;
                if (length < stride * _height) return 0;

                // DIBs are bottom-up; store top-down so WPF can use them directly.
                // DV is interlaced: showing both fields of a 720x480 frame at once gives comb-tooth edges on
                // anything moving. For the preview, keep one field and rebuild the other by averaging its
                // neighbours (field interpolation), much like WinDV's single-field preview. Capture is unaffected.
                for (int y = 0; y < _height; y += 2)
                {
                    int src = (_bottomUp ? _height - 1 - y : y) * stride;
                    Marshal.Copy(buffer + src, _full, y * stride, stride);
                }
                for (int y = 1; y < _height; y += 2)
                {
                    int above = (y - 1) * stride;
                    int below = (y + 1 < _height ? y + 1 : y - 1) * stride;
                    int dst = y * stride;
                    for (int i = 0; i < stride; i++)
                        _full[dst + i] = (byte)((_full[above + i] + _full[below + i] + 1) >> 1);
                }

                Downscale(_full, _width, _height, _glass!, GlassScale);
                if (++_frameCount % 5 == 0)
                {
                    Downscale(_full, _width, _height, _ambient!, AmbientScale);
                    _ambientFresh = true;
                }
                _fresh = true;
                NoteFrame(_glass!);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Preview frame copy failed", ex);
        }
        return 0;
    }

    /// <summary>Takes an already progressive, top-down BGRA frame (from the FFmpeg preview decoder).</summary>
    public void PushTopDown(byte[] frame)
    {
        try
        {
            lock (_gate)
            {
                if (_full is null || frame.Length < _full.Length) return;
                Buffer.BlockCopy(frame, 0, _full, 0, _full.Length);
                Downscale(_full, _width, _height, _glass!, GlassScale);
                if (++_frameCount % 5 == 0)
                {
                    Downscale(_full, _width, _height, _ambient!, AmbientScale);
                    _ambientFresh = true;
                }
                _fresh = true;
                NoteFrame(_glass!);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Preview frame push failed", ex);
        }
    }

    private void NoteFrame(byte[] small)
    {
        long now = Environment.TickCount64;
        Interlocked.Exchange(ref _lastFrameTicks, now);
        if (Spread(small, _width / GlassScale, _height / GlassScale) >= BlankSpread)
            Interlocked.Exchange(ref _lastPictureTicks, now);
    }

    /// <summary>
    /// Standard deviation of brightness across the middle of a top-down BGRA image. The outer eighth on each side
    /// is skipped: DV frames have black edges and head-switching noise along the bottom even when the tape is blank.
    /// </summary>
    internal static double Spread(byte[] bgra, int width, int height)
    {
        int x0 = width / 8, x1 = width - x0, y0 = height / 8, y1 = height - y0;
        double sum = 0, sumSq = 0;
        int n = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * width + x) * 4;
                if (i + 2 >= bgra.Length) break;
                double luma = 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
                sum += luma;
                sumSq += luma * luma;
                n++;
            }
        }
        if (n == 0) return 0;
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
    }

    /// <summary>Box-average downscale of a top-down BGRA buffer.</summary>
    private static void Downscale(byte[] src, int width, int height, byte[] dst, int scale)
    {
        int dw = Math.Max(1, width / scale), dh = Math.Max(1, height / scale);
        int stride = width * 4;
        int area = scale * scale;
        for (int dy = 0; dy < dh; dy++)
        {
            for (int dx = 0; dx < dw; dx++)
            {
                int b = 0, g = 0, r = 0;
                int y0 = dy * scale, x0 = dx * scale;
                for (int y = y0; y < y0 + scale && y < height; y++)
                {
                    int row = y * stride + x0 * 4;
                    for (int x = 0; x < scale; x++)
                    {
                        int i = row + x * 4;
                        b += src[i];
                        g += src[i + 1];
                        r += src[i + 2];
                    }
                }
                int o = (dy * dw + dx) * 4;
                dst[o] = (byte)(b / area);
                dst[o + 1] = (byte)(g / area);
                dst[o + 2] = (byte)(r / area);
                dst[o + 3] = 255;
            }
        }
    }

    /// <summary>Hands the newest frame to <paramref name="use"/> if one arrived since the last call.</summary>
    public bool TryConsume(FrameConsumer use)
    {
        lock (_gate)
        {
            if (!_fresh || _full is null) return false;
            use(new FrameData(_full, _width, _height,
                _glass!, _width / GlassScale, _height / GlassScale,
                _ambientFresh ? _ambient : null, Math.Max(1, _width / AmbientScale), Math.Max(1, _height / AmbientScale)));
            _fresh = false;
            _ambientFresh = false;
            return true;
        }
    }
}

public delegate void FrameConsumer(in FrameData frame);

public readonly record struct FrameData(
    byte[] Full, int Width, int Height,
    byte[] Glass, int GlassWidth, int GlassHeight,
    byte[]? Ambient, int AmbientWidth, int AmbientHeight);
