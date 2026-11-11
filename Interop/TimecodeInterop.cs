using System.Runtime.InteropServices;

namespace TapeRescue.Interop;

/// <summary>TIMECODE_SAMPLE from strmif.h (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TimecodeSample
{
    public long qwTick;
    public short wFrameRate;
    public short wFrameFract;
    /// <summary>For MSDV: BCD packed 0xHHMMSSFF for timecode and counter reads.</summary>
    public int dwFrames;
    public int dwUser;
    /// <summary>Which reading to request (one of the ED_DEVCAP_*_READ values).</summary>
    public int dwFlags;
}

/// <summary>IAMTimecodeReader (strmif.h). Not included in DirectShowLib 1.0.0.</summary>
[ComImport, Guid("9B496CE1-811B-11cf-8C77-00AA006B6814"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAMTimecodeReader
{
    [PreserveSig] int GetTCRMode(int param, out int value);
    [PreserveSig] int SetTCRMode(int param, int value);
    [PreserveSig] int put_VITCLine(int line);
    [PreserveSig] int get_VITCLine(out int line);
    [PreserveSig] int GetTimecode(ref TimecodeSample sample);
}

public static class DvConst
{
    // xprtdefs.h
    public const int ED_DEVCAP_TIMECODE_READ = 4121; // SMPTE-style timecode recorded on DV/Digital8 tape
    public const int ED_DEVCAP_ATN_READ = 5047;      // absolute track number (1394 AV/C)
    public const int ED_DEVCAP_RTC_READ = 5050;      // relative time counter = the camcorder's tape counter
}
