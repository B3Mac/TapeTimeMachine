using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace TapeRescue.Services;

public sealed record MediaInfo(
    string VideoCodec, int Width, int Height, double FramesPerSecond, long FrameCount, double DurationSeconds, bool Interlaced);

public sealed record FfmpegResult(bool Ok, string? Message = null);

/// <summary>Finds and runs FFmpeg/FFprobe. All file work in the app (scan, join, convert) goes through here.</summary>
public static class FfmpegTools
{
    public const string MissingMessage = "This needs FFmpeg. Install it with: winget install Gyan.FFmpeg";

    public static string? FindFfmpeg() => Find("ffmpeg.exe");
    public static string? FindFfprobe() => Find("ffprobe.exe");

    private static string? Find(string exe)
    {
        var dirs = new List<string>
        {
            AppContext.BaseDirectory, // a copy shipped next to the app wins
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
        };
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH", scope) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs)
        {
            try
            {
                string candidate = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Reads the basic facts about a video file. Null if FFprobe is missing or the file has no video.</summary>
    public static async Task<MediaInfo?> ProbeAsync(string file, CancellationToken ct = default)
    {
        string? ffprobe = FindFfprobe();
        if (ffprobe is null) return null;

        var psi = NewProcess(ffprobe);
        foreach (var a in new[]
                 {
                     "-v", "error", "-select_streams", "v:0",
                     "-show_entries", "stream=codec_name,width,height,r_frame_rate,nb_frames,field_order:format=duration",
                     "-of", "default=noprint_wrappers=1", file,
                 })
            psi.ArgumentList.Add(a);

        try
        {
            using var process = Process.Start(psi)!;
            await using var _ = ct.Register(() => TryKill(process));
            var errTask = process.StandardError.ReadToEndAsync(ct);
            string output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await errTask;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) values[line[..eq]] = line[(eq + 1)..];
            }
            if (!values.TryGetValue("width", out var w) || !int.TryParse(w, out int width)) return null;
            int.TryParse(values.GetValueOrDefault("height"), out int height);

            double fps = ParseRate(values.GetValueOrDefault("r_frame_rate"));
            double duration = ParseDouble(values.GetValueOrDefault("duration"));
            long frames = long.TryParse(values.GetValueOrDefault("nb_frames"), out long n) ? n : (long)Math.Round(duration * fps);
            if (duration <= 0 && fps > 0) duration = frames / fps;
            string field = values.GetValueOrDefault("field_order") ?? "";
            string codec = values.GetValueOrDefault("codec_name") ?? "";
            // DV from a camcorder is always interlaced, even when the file doesn't say so.
            bool interlaced = field is "tt" or "bb" or "tb" or "bt" || codec == "dvvideo";
            return new MediaInfo(codec, width, height, fps, frames, duration, interlaced);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Probe failed for {file}", ex);
            return null;
        }
    }

    /// <summary>
    /// Runs FFmpeg with the given arguments (input/output options only) and reports progress 0..1.
    /// Cancelling kills FFmpeg; the caller deletes any partial output.
    /// </summary>
    public static async Task<FfmpegResult> RunAsync(IEnumerable<string> args, double durationSeconds,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return new(false, MissingMessage);

        var psi = NewProcess(ffmpeg);
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-nostats", "-y", "-progress", "pipe:1" }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        Log.Info($"ffmpeg {string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

        try
        {
            using var process = Process.Start(psi)!;
            await using var _ = ct.Register(() => TryKill(process));
            LowerPriority(process);

            var errors = new StringBuilder();
            var readErrors = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    if (errors.Length < 6000) errors.AppendLine(line);
                }
            }, CancellationToken.None);

            string? outLine;
            while ((outLine = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                if (durationSeconds <= 0 || progress is null) continue;
                if ((outLine.StartsWith("out_time_us=", StringComparison.Ordinal) || outLine.StartsWith("out_time_ms=", StringComparison.Ordinal)) &&
                    long.TryParse(outLine.AsSpan(12), out long us) && us >= 0)
                    progress.Report(Math.Clamp(us / 1_000_000.0 / durationSeconds, 0, 1));
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await readErrors;

            ct.ThrowIfCancellationRequested();
            if (process.ExitCode == 0)
            {
                progress?.Report(1);
                return new(true);
            }
            Log.Error($"FFmpeg exited with {process.ExitCode}: {errors}");
            return new(false, LastLine(errors.ToString()));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("FFmpeg run failed", ex);
            return new(false, ex.Message);
        }
    }

    internal static ProcessStartInfo NewProcess(string exe) => new(exe)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
    };

    /// <summary>File work must never take the processor away from a capture that is running.</summary>
    internal static void LowerPriority(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
    }

    internal static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    internal static string Invariant(double value, string format = "0.###") => value.ToString(format, CultureInfo.InvariantCulture);

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    private static double ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate)) return 0;
        var parts = rate.Split('/');
        double num = ParseDouble(parts[0]);
        double den = parts.Length > 1 ? ParseDouble(parts[1]) : 1;
        return den > 0 ? num / den : 0;
    }

    private static double ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
}
