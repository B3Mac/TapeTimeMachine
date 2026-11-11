namespace TapeRescue.Models;

/// <summary>A tape position as hours:minutes:seconds:frames.</summary>
public readonly record struct TapeTime(int Hours, int Minutes, int Seconds, int Frames)
{
    /// <summary>
    /// Decodes MSDV's BCD-packed 0xHHMMSSFF value. Returns null for "no timecode"
    /// (usually 0xFFFFFFFF) or anything that isn't valid BCD.
    /// Flag bits (drop-frame, colour-frame, counter sign) are masked off.
    /// </summary>
    public static TapeTime? FromBcd(int packed)
    {
        uint v = unchecked((uint)packed);
        if (v == 0xFFFFFFFF) return null;

        int? h = Bcd((byte)((v >> 24) & 0x3F));
        int? m = Bcd((byte)((v >> 16) & 0x7F));
        int? s = Bcd((byte)((v >> 8) & 0x7F));
        int? f = Bcd((byte)(v & 0x3F));
        if (h is null || m is null || s is null || f is null) return null;
        if (h > 23 || m > 59 || s > 59 || f > 29) return null;
        return new TapeTime(h.Value, m.Value, s.Value, f.Value);
    }

    private static int? Bcd(byte b)
    {
        int hi = b >> 4, lo = b & 0x0F;
        return hi > 9 || lo > 9 ? null : hi * 10 + lo;
    }

    public double ToSeconds(double framesPerSecond = 29.97) =>
        Hours * 3600 + Minutes * 60 + Seconds + Frames / framesPerSecond;

    public override string ToString() => $"{Hours:00}:{Minutes:00}:{Seconds:00}:{Frames:00}";
}
