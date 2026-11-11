using System.Diagnostics;
using System.IO;
using System.Text;

namespace TapeRescue.Services;

/// <summary>
/// Work that runs after a capture: optional audio clean-up (FFmpeg filters, written to a new MKV
/// with the DV video copied untouched) and handing the file to Topaz Video AI.
/// The captured AVI itself is never modified.
/// </summary>
public static class PostProcess
{
    public sealed record AudioPlugin(string Name, string Filter);

    public static readonly AudioPlugin[] AudioPlugins =
    [
        new("Tape Hiss Reducer", "afftdn=nr=12:nf=-30"),
        new("Voice Denoise", "highpass=f=80,afftdn=nr=18:nf=-28:tn=1"),
        new("Dialogue Leveler", "dynaudnorm=f=250:g=15"),
        new("Warmth & Presence", "equalizer=f=180:t=q:w=1:g=2,equalizer=f=3500:t=q:w=1:g=2.5"),
    ];

    public static readonly string[] TopazModels =
    [
        "Proteus · Auto",
        "Iris · Face Recovery",
        "Nyx · Low Light",
        "Artemis · Medium Quality",
    ];

    public sealed record Result(bool Ok, string? OutputPath = null, string? Message = null);

    public static string? FindFfmpeg()
    {
        var dirs = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
        };
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH", scope) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs)
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    public static string? FindTopaz()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Topaz Labs LLC");
        string known = Path.Combine(root, "Topaz Video AI", "Topaz Video AI.exe");
        if (File.Exists(known)) return known;
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "Topaz Video*.exe", SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }
        catch { return null; }
    }

    /// <summary>Writes "&lt;name&gt; (clean audio).mkv": the DV video copied as-is, audio through the chosen filters (FLAC, lossless).</summary>
    public static async Task<Result> CleanAudioAsync(string input, IReadOnlyList<string> pluginNames, IProgress<double>? progress = null)
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return new(false, Message: "Audio clean-up needs FFmpeg. Install it with: winget install Gyan.FFmpeg");

        var filters = pluginNames
            .Select(n => AudioPlugins.FirstOrDefault(p => p.Name == n)?.Filter)
            .Where(f => f is not null)
            .ToList();
        if (filters.Count == 0) return new(false, Message: "No audio plugins are selected.");

        string folder = Path.GetDirectoryName(input)!;
        string output = AppSettings.UniquePath(folder, Path.GetFileNameWithoutExtension(input) + " (clean audio)", ".mkv");

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-y", "-i", input, "-map", "0:v:0", "-map", "0:a:0",
                     "-c:v", "copy", "-af", string.Join(",", filters), "-c:a", "flac", "-progress", "pipe:1", output })
            psi.ArgumentList.Add(a);

        Log.Info($"Audio clean-up: {ffmpeg} {string.Join(' ', psi.ArgumentList)}");
        try
        {
            using var process = Process.Start(psi)!;
            var errors = new StringBuilder();
            TimeSpan duration = TimeSpan.Zero;
            var readErr = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    if (errors.Length < 8000) errors.AppendLine(line);
                    int d = line.IndexOf("Duration: ", StringComparison.Ordinal);
                    if (d >= 0 && TimeSpan.TryParse(line.Substring(d + 10, 11), out var parsed)) duration = parsed;
                }
            });
            var readOut = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                        long.TryParse(line.AsSpan(12), out long us) && duration > TimeSpan.Zero)
                        progress?.Report(Math.Clamp(us / 1_000_000.0 / duration.TotalSeconds, 0, 1));
                }
            });
            await process.WaitForExitAsync();
            await Task.WhenAll(readErr, readOut);

            if (process.ExitCode == 0) return new(true, output);
            Log.Error($"FFmpeg failed ({process.ExitCode}): {errors}");
            TryDelete(output);
            return new(false, Message: "The audio clean-up didn't finish. The original capture is untouched.");
        }
        catch (Exception ex)
        {
            Log.Error("Audio clean-up", ex);
            TryDelete(output);
            return new(false, Message: $"The audio clean-up couldn't run: {ex.Message}");
        }
    }

    public static bool OpenInTopaz(string file)
    {
        string? topaz = FindTopaz();
        if (topaz is null) return false;
        try
        {
            var psi = new ProcessStartInfo(topaz) { UseShellExecute = false };
            psi.ArgumentList.Add(file);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Opening Topaz Video AI", ex);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
