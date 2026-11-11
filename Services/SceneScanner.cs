using System.Diagnostics;
using System.IO;

namespace TapeRescue.Services;

public sealed record SceneScanResult(
    bool Ok,
    IReadOnlyList<TimeSpan> SceneStarts,
    double FramesPerSecond = 0,
    long FramesScanned = 0,
    string? Message = null);

/// <summary>
/// Finds the scenes in a finished video file. FFmpeg decodes the file to tiny frames (for DV it
/// only reads the coarse picture data, so an hour of tape takes a minute or two) and
/// <see cref="SceneDetector"/> looks for scene changes. Times are exact to the frame.
/// </summary>
public static class SceneScanner
{
    private const int W = 90, H = 60;

    public static async Task<SceneScanResult> ScanAsync(string file, IProgress<double>? progress = null,
        CancellationToken ct = default, SceneDetectorOptions? options = null,
        TimeSpan? start = null, TimeSpan? length = null, Action<SceneTrace>? trace = null,
        Action<long, byte[]>? frameTap = null)
    {
        string? ffmpeg = FfmpegTools.FindFfmpeg();
        if (ffmpeg is null) return new(false, [], Message: FfmpegTools.MissingMessage);
        if (!File.Exists(file)) return new(false, [], Message: $"Couldn't find {file}.");

        MediaInfo? info = await FfmpegTools.ProbeAsync(file, ct);
        if (info is null || info.FramesPerSecond <= 0) return new(false, [], Message: "That file doesn't contain video FFmpeg can read.");

        double fps = info.FramesPerSecond;
        double offset = start?.TotalSeconds ?? 0;
        double span = length?.TotalSeconds ?? Math.Max(0, info.DurationSeconds - offset);
        long expectedFrames = Math.Max(1, (long)(span * fps));

        var psi = FfmpegTools.NewProcess(ffmpeg);
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        if (info.VideoCodec == "dvvideo") args.AddRange(["-lowres", "3"]); // 1/8-size decode: far faster
        if (start is not null) args.AddRange(["-ss", FfmpegTools.Invariant(offset)]);
        args.AddRange(["-i", file]);
        if (length is not null) args.AddRange(["-t", FfmpegTools.Invariant(span)]);
        args.AddRange(
        [
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"scale={W}:{H}:flags=area",
            "-fps_mode", "cfr", // keeps frame number = time even where frames were dropped
            "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1",
        ]);
        foreach (var a in args) psi.ArgumentList.Add(a);

        var detector = new SceneDetector(options ?? new SceneDetectorOptions { FramesPerSecond = fps }) { Trace = trace };

        try
        {
            using var process = Process.Start(psi)!;
            await using var _ = ct.Register(() => FfmpegTools.TryKill(process));
            FfmpegTools.LowerPriority(process);
            var errorsTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            await Task.Run(async () =>
            {
                var stream = process.StandardOutput.BaseStream;
                var frame = new byte[W * H * 3];
                while (true)
                {
                    int read = 0;
                    while (read < frame.Length)
                    {
                        int n = await stream.ReadAsync(frame.AsMemory(read), ct);
                        if (n == 0) break;
                        read += n;
                    }
                    if (read < frame.Length) break;
                    frameTap?.Invoke(detector.FrameCount, frame); // 90×60 BGR, valid only during the call
                    detector.AddFrame(frame, W, H, 3);
                    if (detector.FrameCount % 150 == 0)
                        progress?.Report(Math.Clamp(detector.FrameCount / (double)expectedFrames, 0, 1));
                }
            }, ct);

            await process.WaitForExitAsync(CancellationToken.None);
            string errors = await errorsTask;
            ct.ThrowIfCancellationRequested();

            if (detector.FrameCount == 0)
            {
                Log.Error($"Scene scan read no frames from {file}: {errors}");
                return new(false, [], fps, 0, "FFmpeg couldn't read the video in that file.");
            }

            var starts = detector.Finish().Select(f => TimeSpan.FromSeconds(offset + f / fps)).ToList();
            progress?.Report(1);
            Log.Info($"Scene scan: {file}, {detector.FrameCount} frames, {starts.Count} scenes");
            return new(true, starts, fps, detector.FrameCount);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("Scene scan failed", ex);
            return new(false, [], fps, detector.FrameCount, $"The scene scan couldn't run: {ex.Message}");
        }
    }
}
