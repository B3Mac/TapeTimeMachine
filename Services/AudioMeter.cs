using System.Runtime.InteropServices;
using DirectShowLib;

namespace TapeRescue.Services;

/// <summary>
/// Sits in the preview's audio path (a Sample Grabber before the speakers) and keeps the latest
/// peak level so the UI can draw a real level meter. Also remembers the audio format.
/// </summary>
[ComVisible(true)]
public sealed class AudioMeter : ISampleGrabberCB
{
    private short[] _scratch = new short[8192];
    private volatile float _peak;
    private long _lastTicks;

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int BitsPerSample { get; private set; }

    /// <summary>Peak of the most recent audio buffer, 0..1. Zero when no audio has arrived for a while.</summary>
    public float Peak => Environment.TickCount64 - Interlocked.Read(ref _lastTicks) < 500 ? _peak : 0f;

    public void Configure(int sampleRate, int channels, int bitsPerSample)
    {
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
    }

    public void Reset()
    {
        SampleRate = Channels = BitsPerSample = 0;
        _peak = 0;
    }

    public int SampleCB(double sampleTime, IMediaSample sample) => 0;

    public int BufferCB(double sampleTime, IntPtr buffer, int length)
    {
        if (BitsPerSample != 16 || length < 2) return 0;
        int count = length / 2;
        if (_scratch.Length < count) _scratch = new short[count];
        Marshal.Copy(buffer, _scratch, 0, count);
        int max = 0;
        for (int i = 0; i < count; i++)
        {
            int v = Math.Abs((int)_scratch[i]);
            if (v > max) max = v;
        }
        _peak = max / 32768f;
        Interlocked.Exchange(ref _lastTicks, Environment.TickCount64);
        return 0;
    }
}
