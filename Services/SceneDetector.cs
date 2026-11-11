namespace TapeRescue.Services;

public sealed class SceneDetectorOptions
{
    public double FramesPerSecond { get; init; } = 30000.0 / 1001.0;

    /// <summary>Scenes shorter than this don't get their own start.</summary>
    public double MinSceneSeconds { get; init; } = 3.0;

    /// <summary>1.0 is the default. Higher finds more scene changes (and more false ones); lower finds fewer.</summary>
    public double Sensitivity { get; init; } = 1.0;
}

/// <summary>Per-frame numbers for tuning. All are 0 (identical) to 1 (completely different).</summary>
public readonly record struct SceneTrace(long Frame, double Hist, double Layout, double Colour, double Step, double Span);

/// <summary>
/// Finds scene changes by picture content ("optical" detection), for tapes that carry no recording date.
/// Feed it small frames in order (about 90×60, BGR or BGRA). Each frame is reduced to a brightness
/// histogram plus a coarse grid of brightness and colour, and compared with earlier frames.
///
/// A frame starts a new scene when:
///  - the picture is clearly different two frames after it from two frames before it (on interlaced
///    tape a cut is smeared over two frames, so the change is measured across a short span);
///  - the jump is abrupt and the picture is calm on both sides of it. A pan, a zoom, a jerk of the
///    camera or someone walking past builds up and dies away over several frames instead;
///  - the picture does not return to what it was a moment later, so camera flashes and tape dropouts
///    don't count.
/// Blank stretches (black, blue or grey screen) never start a scene; the first picture after one does.
///
/// Tuned on Hi8 home video played through a Digital8 camcorder. It favours missing a cut (between two
/// similar shots, or made while the camera was swinging) over marking a scene where the camera merely moved.
/// </summary>
public sealed class SceneDetector
{
    private const int HistBins = 32;
    private const int GridW = 8, GridH = 6, Cells = GridW * GridH;
    private const int Span = 4;          // frames a cut is measured across (2 before the cut to 2 after)
    private const int History = 12;      // frames of "normal motion" before a candidate
    private const int Lookahead = 6;     // frames looked at after a candidate before deciding
    private const int RingSize = 32, Mask = RingSize - 1;

    private const double CutThreshold = 0.30;    // change across the span that a cut must reach
    private const double TriggerFactor = 0.5;    // single-frame change (× threshold) that makes a frame worth checking
    private const double ShoulderFactor = 1.8;   // the peak must be this many times the change in the frames beside the cut
    private const double CalmFactor = 4.0;       // ...and the jump this many times the typical change before and after it
    private const double SameSceneScore = 0.12;  // below this, two frames show the same scene
    private const double BlankStdDev = 5.0;      // luma spread under this is a blank screen
    private const int MinBlankRun = 8;           // frames of blank before "picture is back" counts
    private const double BlankExitStrength = 2;  // a start after a blank outranks any cut

    private sealed class Features
    {
        public readonly float[] Hist = new float[HistBins];
        public readonly float[] Y = new float[Cells], U = new float[Cells], V = new float[Cells];
        public bool Blank;

        public void CopyFrom(Features other)
        {
            other.Hist.CopyTo(Hist, 0);
            other.Y.CopyTo(Y, 0);
            other.U.CopyTo(U, 0);
            other.V.CopyTo(V, 0);
            Blank = other.Blank;
        }
    }

    private readonly Features[] _ring = new Features[RingSize];
    private readonly double[] _step = new double[RingSize];   // change from the previous frame
    private readonly double[] _span = new double[RingSize];   // change from Span frames earlier
    private readonly int[] _cellCount = new int[Cells];
    private readonly double[] _scratch = new double[History];
    private readonly List<long> _starts = [0];
    private readonly List<double> _strengths = [BlankExitStrength];
    private readonly Features _beforeLastStart = new();
    private bool _hasBeforeLastStart;
    private readonly double _threshold;
    private readonly long _minSceneFrames, _mergeFrames;
    private long _count;
    private long _evaluated;   // next frame index to evaluate
    private int _blankRun;

    /// <summary>Optional per-frame trace for tuning.</summary>
    public Action<SceneTrace>? Trace { get; set; }

    public SceneDetector(SceneDetectorOptions? options = null)
    {
        options ??= new SceneDetectorOptions();
        _threshold = CutThreshold / Math.Clamp(options.Sensitivity, 0.25, 4.0);
        _minSceneFrames = Math.Max(1, (long)Math.Round(options.MinSceneSeconds * options.FramesPerSecond));
        _mergeFrames = Math.Min(_minSceneFrames, (long)Math.Round(0.75 * options.FramesPerSecond));
        for (int i = 0; i < RingSize; i++) _ring[i] = new Features();
    }

    public long FrameCount => _count;

    /// <summary>Scene starts found so far, as frame numbers. Always begins with 0. Recent entries can still move.</summary>
    public IReadOnlyList<long> SceneStartFrames => _starts;

    public void AddFrame(ReadOnlySpan<byte> pixels, int width, int height, int bytesPerPixel)
    {
        if (bytesPerPixel is not (3 or 4)) throw new ArgumentOutOfRangeException(nameof(bytesPerPixel));
        if (pixels.Length < width * height * bytesPerPixel) throw new ArgumentException("Frame buffer is too small.", nameof(pixels));

        long i = _count;
        Features f = _ring[i & Mask];
        Extract(pixels, width, height, bytesPerPixel, f);
        if (i == 0)
        {
            _step[0] = _span[0] = 0;
        }
        else
        {
            double step = Score(_ring[(i - 1) & Mask], f, out double hd, out double bd, out double cd);
            double span = Score(_ring[Math.Max(0, i - Span) & Mask], f, out _, out _, out _);
            _step[i & Mask] = step;
            _span[i & Mask] = span;
            Trace?.Invoke(new SceneTrace(i, hd, bd, cd, step, span));
        }
        _count++;

        while (_evaluated <= i - Lookahead) Evaluate(_evaluated++);
    }

    /// <summary>Call after the last frame. Returns every scene start as a frame number, beginning with 0.</summary>
    public IReadOnlyList<long> Finish()
    {
        while (_evaluated < _count) Evaluate(_evaluated++);
        return _starts;
    }

    private void Evaluate(long c)
    {
        Features f = _ring[c & Mask];

        // Picture returning after a blank stretch always starts a scene.
        if (!f.Blank && _blankRun >= MinBlankRun)
        {
            // Whatever started just before the blank (the noisy last moments of a recording) wasn't a
            // scene worth marking; the scene starts here.
            long blankStart = c - _blankRun;
            if (_starts.Count > 1 && blankStart - _starts[^1] < _minSceneFrames) RemoveLastStart();
            _blankRun = 0;
            AddStart(c, BlankExitStrength);
            return;
        }
        _blankRun = f.Blank ? _blankRun + 1 : 0;
        if (c == 0 || f.Blank) return;

        // Worth a look: a noticeable jump that is the peak of its neighbourhood.
        long last = _count - 1;
        double d = _step[c & Mask];
        if (d < _threshold * TriggerFactor) return;
        if (d < _step[(c - 1) & Mask]) return;
        if (c + 1 <= last && d < _step[(c + 1) & Mask]) return;

        // The picture must really be different afterwards: compare just before the cut with just after.
        long after = Math.Min(c + Span / 2, last), before = Math.Max(0, c - Span / 2);
        double change = after - before == Span
            ? _span[after & Mask]
            : Score(_ring[before & Mask], _ring[after & Mask], out _, out _, out _);
        if (change < _threshold) return;

        // A cut is abrupt: the frames on either side of it are calm. A quick jerk of the camera builds
        // up and dies away over several frames instead. The cut itself may take two frames, when it
        // falls between the fields of an interlaced frame.
        double prev = _step[(c - 1) & Mask], next = c + 1 <= last ? _step[(c + 1) & Mask] : 0;
        long lo = prev > next ? c - 2 : c - 1, hi = prev > next ? c + 1 : c + 2;
        double shoulder = Math.Max(lo >= 1 ? _step[lo & Mask] : 0, hi <= last ? _step[hi & Mask] : 0);
        if (d < ShoulderFactor * shoulder) return;

        // It must also stand out from the motion on both sides. In a pan, a zoom, a shaky shot or when
        // someone walks past, many frames in a row differ by a similar amount.
        double pair = d + Math.Max(prev, next);
        if (pair < CalmFactor * MedianStep(Math.Max(1, c - 1 - History), c - 2)) return;
        if (pair < CalmFactor * MedianStep(c + 2, Math.Min(c + Lookahead, last))) return;

        // If the picture a moment later matches the picture a moment earlier, this was a flash or a
        // dropout (or the return from one), not a new scene.
        long upper = Math.Min(c + Lookahead, last);
        for (long b = Math.Max(0, c - 1 - Lookahead); b < c; b++)
        {
            Features earlier = _ring[b & Mask];
            if (earlier.Blank) continue;
            for (long j = c; j <= upper; j++)
            {
                if (Score(earlier, _ring[j & Mask], out _, out _, out _) < SameSceneScore) return;
            }
        }

        // A longer disturbance (tracking noise for a second or two) looks like a cut into noise and a
        // cut back out. If what follows matches what came before the last start, drop that start.
        if (_hasBeforeLastStart && _starts.Count > 1 && c - _starts[^1] < _minSceneFrames)
        {
            for (long j = c; j <= upper; j++)
            {
                if (Score(_beforeLastStart, _ring[j & Mask], out _, out _, out _) < SameSceneScore)
                {
                    RemoveLastStart();
                    return;
                }
            }
        }

        AddStart(c, change);
    }

    /// <summary>Median single-frame change over frames [from, to]. Zero when the range is empty.</summary>
    private double MedianStep(long from, long to)
    {
        int n = 0;
        for (long j = from; j <= to && n < _scratch.Length; j++) _scratch[n++] = _step[j & Mask];
        if (n == 0) return 0;
        Array.Sort(_scratch, 0, n);
        return _scratch[n / 2];
    }

    private void AddStart(long frame, double strength)
    {
        long gap = frame - _starts[^1];
        if (gap >= _minSceneFrames)
        {
            _starts.Add(frame);
            _strengths.Add(strength);
            long b = Math.Max(0, frame - Span / 2);
            if (_count - b < RingSize)
            {
                _beforeLastStart.CopyFrom(_ring[b & Mask]);
                _hasBeforeLastStart = !_beforeLastStart.Blank;
            }
            else _hasBeforeLastStart = false;
            return;
        }

        // Too soon after the last start to be a scene of its own. The first moments of the tape always
        // belong to the opening scene. Otherwise the marker moves here when this follows almost at once
        // (a burst of noise between two shots: the picture proper begins here) or when this is the
        // bigger change of the two. The earlier "before" picture is kept either way, so a return to it
        // is still recognised.
        if (_starts.Count == 1) return;
        if (gap <= _mergeFrames || strength > _strengths[^1])
        {
            _starts[^1] = frame;
            _strengths[^1] = Math.Max(strength, _strengths[^1]);
        }
    }

    private void RemoveLastStart()
    {
        _starts.RemoveAt(_starts.Count - 1);
        _strengths.RemoveAt(_strengths.Count - 1);
        _hasBeforeLastStart = false;
    }

    /// <summary>0 = identical, 1 = completely different.</summary>
    private static double Score(Features a, Features b, out double hist, out double layout, out double colour)
    {
        double h = 0, y = 0, c = 0;
        for (int i = 0; i < HistBins; i++) h += Math.Abs(a.Hist[i] - b.Hist[i]);
        for (int i = 0; i < Cells; i++)
        {
            y += Math.Abs(a.Y[i] - b.Y[i]);
            c += Math.Abs(a.U[i] - b.U[i]) + Math.Abs(a.V[i] - b.V[i]);
        }
        hist = h * 0.5;
        layout = y / (Cells * 255.0);
        colour = c / (Cells * 2 * 255.0);
        return 0.5 * hist + 0.5 * Math.Min(1.0, 4.0 * layout + 4.0 * colour);
    }

    private void Extract(ReadOnlySpan<byte> px, int width, int height, int bpp, Features f)
    {
        // Ignore the edges: analog tapes have black borders at the sides and head-switching noise along the bottom.
        int x0 = width * 4 / 100, x1 = width - x0;
        int y0 = height * 4 / 100, y1 = height - height * 8 / 100;
        int iw = x1 - x0, ih = y1 - y0;

        Array.Clear(f.Hist);
        Array.Clear(f.Y);
        Array.Clear(f.U);
        Array.Clear(f.V);
        Array.Clear(_cellCount);

        long sum = 0, sumSq = 0;
        int stride = width * bpp;
        for (int y = y0; y < y1; y++)
        {
            int row = y * stride;
            int gy = (y - y0) * GridH / ih * GridW;
            for (int x = x0; x < x1; x++)
            {
                int p = row + x * bpp;
                int b = px[p], g = px[p + 1], r = px[p + 2];
                int luma = (77 * r + 150 * g + 29 * b) >> 8;
                int cell = gy + (x - x0) * GridW / iw;
                f.Hist[luma >> 3]++;
                f.Y[cell] += luma;
                f.U[cell] += (b - luma + 255) >> 1;
                f.V[cell] += (r - luma + 255) >> 1;
                _cellCount[cell]++;
                sum += luma;
                sumSq += luma * luma;
            }
        }

        int total = iw * ih;
        for (int i = 0; i < HistBins; i++) f.Hist[i] /= total;
        for (int i = 0; i < Cells; i++)
        {
            int n = Math.Max(1, _cellCount[i]);
            f.Y[i] /= n;
            f.U[i] /= n;
            f.V[i] /= n;
        }

        double mean = (double)sum / total;
        double variance = Math.Max(0, (double)sumSq / total - mean * mean);
        f.Blank = Math.Sqrt(variance) < BlankStdDev;
    }
}
