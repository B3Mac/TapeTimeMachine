using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TapeRescue.Services;

/// <summary>Where captures go and what they're called. Saved to %LOCALAPPDATA%\TapeTimeMachine\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>DV is about 3.6 MB per second of tape.</summary>
    public const double DvBytesPerHour = 3_600_000d * 3600d;

    /// <summary>Don't start (and stop) a capture with less than this free.</summary>
    public const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;

    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TapeTimeMachine");

    private static string FilePath => Path.Combine(DataFolder, "settings.json");

    public string OutputFolder { get; set; } = DefaultFolder();
    public string FileName { get; set; } = "Tape 1";

    /// <summary>Audio clean-up run after each capture, in order (names from <see cref="PostProcess.AudioPlugins"/>).</summary>
    public List<string> AudioPlugins { get; set; } = [];

    /// <summary>The Topaz Video AI model the user prefers (shown as a reminder; Topaz picks it in its own window).</summary>
    public string TopazModel { get; set; } = "Proteus · Auto";

    /// <summary>Open the finished file in Topaz Video AI after each capture.</summary>
    public bool SendToTopaz { get; set; }

    /// <summary>Find the scenes in each capture and save them as chapters next to it.</summary>
    public bool AddChapters { get; set; } = true;

    /// <summary>Make a ready-to-watch copy after each capture.</summary>
    public bool ConvertAfterCapture { get; set; }

    /// <summary>Which copy to make (an id from <see cref="Converter.Presets"/>).</summary>
    public string ConvertPreset { get; set; } = Converter.Jellyfin.Id;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.OutputFolder) || ValidateFolder(loaded.OutputFolder) is not null)
                        loaded.OutputFolder = DefaultFolder();
                    if (string.IsNullOrWhiteSpace(loaded.FileName)) loaded.FileName = "Tape 1";
                    loaded.AudioPlugins = (loaded.AudioPlugins ?? [])
                        .Where(n => PostProcess.AudioPlugins.Any(p => p.Name == n)).Distinct().ToList();
                    if (!PostProcess.TopazModels.Contains(loaded.TopazModel)) loaded.TopazModel = PostProcess.TopazModels[0];
                    loaded.ConvertPreset = Converter.PresetById(loaded.ConvertPreset).Id;
                    return loaded;
                }
            }
        }
        catch (Exception ex) { Log.Error("Reading settings failed", ex); }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Error("Saving settings failed", ex); }
    }

    /// <summary>Videos\Tape Time Machine until the user picks somewhere else (the choice is remembered).</summary>
    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Tape Time Machine");

    /// <summary>Returns a plain-language problem with the folder, or null when it's fine to save there.</summary>
    public static string? ValidateFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            return "Choose a full folder path, such as D:\\Home Videos.";

        string full = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        string[] system =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtualStore"),
        ];
        foreach (var root in system.Where(r => !string.IsNullOrEmpty(r)))
        {
            if (full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return "That's a Windows system folder. Choose a folder of your own, such as one in Videos.";
        }
        return null;
    }

    /// <summary>Removes characters Windows doesn't allow in file names. Never returns empty.</summary>
    public static string CleanFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        if (cleaned.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[..^4].TrimEnd();
        return cleaned.Length == 0 ? "Tape" : cleaned;
    }

    /// <summary>Never overwrites: "Name.avi", then "Name (2).avi", "Name (3).avi"…</summary>
    public static string UniquePath(string folder, string name, string extension)
    {
        string path = Path.Combine(folder, name + extension);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{name} ({i}){extension}");
        return path;
    }

    /// <summary>"Tape 7" becomes "Tape 8"; names without a trailing number stay as they are.</summary>
    public static string NextName(string name)
    {
        var m = Regex.Match(name, @"^(.*?)(\d+)$");
        if (!m.Success) return name;
        string digits = m.Groups[2].Value;
        long next = long.Parse(digits) + 1;
        return m.Groups[1].Value + next.ToString().PadLeft(digits.Length, '0');
    }
}
