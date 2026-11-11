using System.IO;

namespace TapeRescue.Services;

public sealed record ConvertPreset(string Id, string Name, string Description, string Extension);

public sealed class ConvertOptions
{
    /// <summary>Chapters to put in the new file (for example the scenes found by <see cref="SceneScanner"/>).</summary>
    public IReadOnlyList<Chapter>? Chapters { get; init; }

    /// <summary>Where the new file goes. Default: next to the original.</summary>
    public string? OutputFolder { get; init; }

    /// <summary>Convert only part of the file (for a quick sample).</summary>
    public TimeSpan? Start { get; init; }
    public TimeSpan? Length { get; init; }
}

public sealed record ConvertResult(bool Ok, string? OutputPath = null, string? Message = null);

/// <summary>
/// Makes a copy of a capture that plays everywhere. The captured DV file is large and interlaced, and
/// browsers can't play it, so Jellyfin has to convert it every time someone watches. These presets do
/// that conversion once, properly. The original capture is never changed.
/// </summary>
public static class Converter
{
    public static readonly ConvertPreset Jellyfin = new("jellyfin", "Jellyfin and web (MP4)",
        "H.264 video with AAC sound. Plays directly in Jellyfin, browsers, phones and TVs.", ".mp4");

    public static readonly ConvertPreset Smaller = new("smaller", "Smaller file (HEVC MP4)",
        "About half the size for the same quality. Plays directly on newer devices; Jellyfin converts it on the fly for older ones.", ".mp4");

    public static readonly ConvertPreset Archive = new("archive", "Archive copy (MKV)",
        "The original picture and sound, not re-encoded, in an MKV file with chapters.", ".mkv");

    public static readonly ConvertPreset[] Presets = [Jellyfin, Smaller, Archive];

    public static ConvertPreset PresetById(string? id) => Presets.FirstOrDefault(p => p.Id == id) ?? Jellyfin;

    public static async Task<ConvertResult> ConvertAsync(string input, ConvertPreset preset, ConvertOptions? options = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        options ??= new ConvertOptions();
        if (!File.Exists(input)) return new(false, Message: $"Couldn't find {input}.");
        if (FfmpegTools.FindFfmpeg() is null) return new(false, Message: FfmpegTools.MissingMessage);

        MediaInfo? info = await FfmpegTools.ProbeAsync(input, ct);
        if (info is null) return new(false, Message: "That file doesn't contain video FFmpeg can read.");

        string folder = options.OutputFolder ?? Path.GetDirectoryName(input)!;
        if (AppSettings.ValidateFolder(folder) is string problem) return new(false, Message: problem);
        Directory.CreateDirectory(folder);
        string output = AppSettings.UniquePath(folder, Path.GetFileNameWithoutExtension(input), preset.Extension);

        double offset = options.Start?.TotalSeconds ?? 0;
        double duration = options.Length?.TotalSeconds ?? Math.Max(0, info.DurationSeconds - offset);

        string? metadata = null;
        var args = new List<string>();
        if (options.Start is not null) args.AddRange(["-ss", FfmpegTools.Invariant(offset)]);
        args.AddRange(["-i", input]);
        if (options.Chapters is { Count: > 0 } chapters)
        {
            // Chapter times are for the whole file; shift them when converting only a part of it.
            var shifted = chapters
                .Select(c => new Chapter(c.Start - TimeSpan.FromSeconds(offset), c.Name))
                .Where(c => c.Start >= TimeSpan.Zero && c.Start.TotalSeconds < duration)
                .ToList();
            if (shifted.Count > 0)
            {
                metadata = Path.Combine(Path.GetTempPath(), $"ttm-chapters-{Guid.NewGuid():N}.txt");
                ChapterWriter.WriteFfMetadata(metadata, shifted, TimeSpan.FromSeconds(duration));
                args.AddRange(["-i", metadata, "-map_metadata", "1", "-map_chapters", "1"]);
            }
        }
        if (options.Length is not null) args.AddRange(["-t", FfmpegTools.Invariant(duration)]);

        if (preset.Id == Archive.Id)
        {
            args.AddRange(["-map", "0:v:0", "-map", "0:a?", "-c", "copy"]);
        }
        else
        {
            args.AddRange(["-map", "0:v:0", "-map", "0:a:0?"]);

            // Tape video is interlaced: each frame holds two moments in time. Turning every field into
            // its own frame (60 a second for NTSC) keeps motion as smooth as it was on a TV. Tape pixels
            // aren't square either, so the picture is resized to its true shape (640×480 for NTSC 4:3).
            var filters = new List<string>();
            if (info.Interlaced) filters.Add("bwdif=mode=send_field");
            filters.Add("scale=w='trunc(ih*dar/2)*2':h=ih:flags=lanczos");
            filters.Add("setsar=1");
            args.AddRange(["-vf", string.Join(",", filters)]);

            if (preset.Id == Smaller.Id)
                args.AddRange(["-c:v", "libx265", "-preset", "medium", "-crf", "22", "-tag:v", "hvc1", "-x265-params", "log-level=error"]);
            else
                args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "19", "-profile:v", "high"]);

            args.AddRange(["-pix_fmt", "yuv420p"]);
            if (info.Height <= 576) // standard-definition colour, so players don't guess HD colour
                args.AddRange(info.Height == 576
                    ? ["-color_primaries", "bt470bg", "-color_trc", "gamma28", "-colorspace", "bt470bg"]
                    : ["-color_primaries", "smpte170m", "-color_trc", "smpte170m", "-colorspace", "smpte170m"]);
            args.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart"]);
        }
        args.Add(output);

        try
        {
            FfmpegResult run = await FfmpegTools.RunAsync(args, duration, progress, ct);
            if (run.Ok && File.Exists(output) && new FileInfo(output).Length > 0) return new(true, output);
            TryDelete(output);
            return new(false, Message: run.Message is null
                ? "The conversion didn't finish. The original capture is untouched."
                : $"The conversion didn't finish ({run.Message}). The original capture is untouched.");
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
            throw;
        }
        finally
        {
            if (metadata is not null) TryDelete(metadata);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
