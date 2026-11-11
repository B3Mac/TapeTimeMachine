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

/// <param name="FrameExact">The live MP4 has exactly the AVI's frames, so sound made from the AVI lines up with it.</param>
public sealed record LiveMp4Finish(bool Ok, string? OutputPath = null, string? Message = null, bool FrameExact = false, double DurationSeconds = 0);

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
                args.AddRange(["-c:v", "libx265", "-preset", "medium", "-crf", "22", "-tag:v", "hvc1", "-x265-params", "log-level=error", "-pix_fmt", "yuv420p"]);
            else
                args.AddRange(LiveMp4Encoder.VideoArgs); // the same picture the live MP4 makes during capture

            if (info.Height <= 576) // standard-definition colour, so players don't guess HD colour
                args.AddRange(LiveMp4Encoder.ColourArgs(pal: info.Height == 576));
            args.AddRange(LiveMp4Encoder.AudioArgs);
            args.AddRange(["-movflags", "+faststart"]);
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

    /// <summary>
    /// Turns the MP4 made during capture into the final "Name.mp4" straight after the capture, before the slow
    /// steps: checks it has the whole tape (against the AVI's frame count), then copies it, without re-encoding,
    /// into a normal MP4 laid out for streaming. Chapters and cleaned sound go in afterwards with
    /// <see cref="AddToMp4Async"/>. Fails (so the caller converts the AVI instead) when the live copy is missing
    /// frames. The live ".part" file is only deleted once the MP4 is written.
    /// </summary>
    /// <param name="captureDropped">
    /// Frames the capture itself dropped (bad patches of tape). The AVI fills each gap so its timing stays right;
    /// the live copy never receives those frames, so it is expected to have that many fewer.
    /// </param>
    public static async Task<LiveMp4Finish> FinishLiveMp4Async(LiveMp4Result live, string aviPath, long captureDropped = 0,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!live.Ok || live.PartPath is null || !File.Exists(live.PartPath))
            return new(false, Message: live.Message ?? "The live MP4 wasn't written.");

        MediaInfo? avi = await FfmpegTools.ProbeAsync(aviPath, ct);
        if (avi is null) return new(false, Message: "Couldn't read the capture to check the live MP4 against it.");

        // The live copy comes from the preview's frames; it must have (virtually) every frame the camcorder sent.
        long missing = avi.FrameCount - Math.Max(0, captureDropped) - live.FramesIn;
        long allowed = Math.Max(30, avi.FrameCount / 500); // a second, or 0.2% of a long tape
        Log.Info($"Live MP4 check: AVI {avi.FrameCount} frames ({captureDropped} of them filled-in drops), live {live.FramesIn} in, {live.Dropped} dropped, missing {missing} (allowed {allowed})");
        if (live.Dropped > 0 || Math.Abs(missing) > allowed)
            return new(false, Message: live.Dropped > 0
                ? $"The live MP4 fell behind and missed {live.Dropped:N0} frames."
                : $"The live MP4 is missing {Math.Abs(missing):N0} frames.");

        string folder = Path.GetDirectoryName(aviPath)!;
        string output = AppSettings.UniquePath(folder, Path.GetFileNameWithoutExtension(aviPath), ".mp4");
        string[] args = ["-i", live.PartPath, "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-movflags", "+faststart", output];
        try
        {
            FfmpegResult run = await FfmpegTools.RunAsync(args, avi.DurationSeconds, progress, ct);
            if (run.Ok && File.Exists(output) && new FileInfo(output).Length > 0)
            {
                TryDelete(live.PartPath);
                Log.Info($"Live MP4 saved: {output}");
                return new(true, output, FrameExact: missing == 0 && captureDropped <= 0,
                    DurationSeconds: avi.DurationSeconds - Math.Max(0, captureDropped) / (avi.FramesPerSecond > 0 ? avi.FramesPerSecond : 29.97));
            }
            TryDelete(output);
            return new(false, Message: run.Message ?? "The live MP4 couldn't be finished.");
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
            throw;
        }
    }

    /// <summary>
    /// Puts chapters and/or cleaned-up sound into an MP4 that already exists, without re-encoding the picture.
    /// Writes a temporary file next to it and swaps it in only when it's complete, so stopping or failing
    /// leaves the MP4 as it was.
    /// </summary>
    public static async Task<ConvertResult> AddToMp4Async(string mp4Path, double durationSeconds, IReadOnlyList<Chapter>? chapters,
        string? cleanAudioPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        bool useCleanAudio = cleanAudioPath is not null && File.Exists(cleanAudioPath);
        var inFile = (chapters ?? []).Where(c => c.Start.TotalSeconds < durationSeconds).ToList();
        if (!useCleanAudio && inFile.Count == 0) return new(true, mp4Path);

        string temp = Path.Combine(Path.GetDirectoryName(mp4Path)!, Path.GetFileNameWithoutExtension(mp4Path) + ".adding.tmp");
        string? metadata = null;
        var args = new List<string> { "-i", mp4Path };
        int next = 1;
        int audioInput = 0;
        if (useCleanAudio)
        {
            args.AddRange(["-i", cleanAudioPath!]);
            audioInput = next++;
        }
        if (inFile.Count > 0)
        {
            metadata = Path.Combine(Path.GetTempPath(), $"ttm-chapters-{Guid.NewGuid():N}.txt");
            ChapterWriter.WriteFfMetadata(metadata, inFile, TimeSpan.FromSeconds(durationSeconds));
            args.AddRange(["-i", metadata, "-map_chapters", next.ToString()]);
            next++;
        }
        args.AddRange(["-map", "0:v:0", "-map", $"{audioInput}:a:0?", "-c:v", "copy"]);
        if (useCleanAudio) args.AddRange(LiveMp4Encoder.AudioArgs);
        else args.AddRange(["-c:a", "copy"]);
        args.AddRange(["-movflags", "+faststart", "-f", "mp4", temp]);

        try
        {
            FfmpegResult run = await FfmpegTools.RunAsync(args, durationSeconds, progress, ct);
            if (run.Ok && File.Exists(temp) && new FileInfo(temp).Length > 0)
            {
                File.Move(temp, mp4Path, overwrite: true);
                return new(true, mp4Path);
            }
            TryDelete(temp);
            return new(false, Message: run.Message ?? "The chapters couldn't be added to the MP4.");
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return new(false, Message: $"The chapters couldn't be added to the MP4 ({ex.Message}).");
        }
        finally
        {
            if (metadata is not null) TryDelete(metadata);
        }
    }

    /// <summary>
    /// The finished live ".part" file is a complete, playable MP4 (just not laid out for streaming). When
    /// finishing it is stopped, keep it as "Name.mp4" rather than losing the tape's MP4.
    /// </summary>
    public static string? KeepLivePart(string partPath, string aviPath)
    {
        if (!File.Exists(partPath)) return null;
        try
        {
            string output = AppSettings.UniquePath(Path.GetDirectoryName(aviPath)!, Path.GetFileNameWithoutExtension(aviPath), ".mp4");
            File.Move(partPath, output);
            Log.Info($"Kept the live MP4 as {output}");
            return output;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't keep the live MP4 {partPath}: {ex.Message}");
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
