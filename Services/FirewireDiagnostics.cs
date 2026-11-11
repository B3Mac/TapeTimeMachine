using System.Management;

namespace TapeRescue.Services;

public enum FirewireState { Ok, NoController, DriverProblem, Unknown }

public sealed record FirewireReport(FirewireState State, string? ControllerName = null, int ErrorCode = 0);

/// <summary>Explains *why* no camcorder is visible: no 1394 port, a broken driver, or just no camcorder.</summary>
public static class FirewireDiagnostics
{
    public static FirewireReport CheckController()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = '1394'");
            var controllers = searcher.Get().Cast<ManagementBaseObject>()
                .Select(o => (Name: o["Name"] as string, Code: Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0)))
                .ToList();

            if (controllers.Count == 0) return new(FirewireState.NoController);
            var working = controllers.FirstOrDefault(c => c.Code == 0);
            if (working.Name is not null) return new(FirewireState.Ok, working.Name);
            return new(FirewireState.DriverProblem, controllers[0].Name, controllers[0].Code);
        }
        catch (Exception ex)
        {
            Log.Error("FireWire controller check failed", ex);
            return new(FirewireState.Unknown);
        }
    }

    /// <summary>The camcorder's Device Manager name (e.g. "Sony DV Camcorder") rather than the generic MSDV filter name.</summary>
    public static string? GetCamcorderName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, PNPClass FROM Win32_PnPEntity WHERE DeviceID LIKE 'AVC%'");
            return searcher.Get().Cast<ManagementBaseObject>()
                .Where(o => string.Equals(o["PNPClass"] as string, "Image", StringComparison.OrdinalIgnoreCase))
                .Select(o => o["Name"] as string)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        }
        catch (Exception ex)
        {
            Log.Error("Camcorder name lookup failed", ex);
            return null;
        }
    }
}
