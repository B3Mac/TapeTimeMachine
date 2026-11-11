#if DEBUG
using System.IO;
using TapeRescue.Services;

namespace TapeRescue;

/// <summary>
/// Debug-only hardware check: run with --selftest=&lt;folder&gt;. With the real camcorder it plays,
/// pauses, records a 10-second capture into &lt;folder&gt; (never the user's library), runs the audio
/// clean-up on it, and returns the tape to the mode it found it in. Every step is written to the log
/// with a "SELFTEST" prefix. Never compiled into Release builds.
/// </summary>
public partial class MainWindow
{
    private static string? SelfTestFolder =>
        Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--selftest=", StringComparison.OrdinalIgnoreCase))?[11..];

    /// <summary>Optional custom run: --selftest-steps=rew:3000,play:6000,stop:1500 (no capture).</summary>
    private static string? SelfTestSteps =>
        Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--selftest-steps=", StringComparison.OrdinalIgnoreCase))?[17..];

    /// <summary>--preview-variant=noclock|nosink|half, read before connecting (see TransportController).</summary>
    public static string? PreviewVariantArg =>
        Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--preview-variant=", StringComparison.OrdinalIgnoreCase))?[18..];

    private async Task RunSelfTestAsync(string folder)
    {
        try
        {
            Log.Info("SELFTEST start");
            for (int i = 0; i < 40 && !_connected; i++) await Task.Delay(250);
            if (!_connected)
            {
                Log.Info($"SELFTEST end: not connected ({_connection.State} {_connection.TechnicalDetail})");
                return;
            }
            await Task.Delay(1500);
            TransportMode initial = _mode;
            Probe("connected");

            if (SelfTestSteps is string steps)
            {
                foreach (var step in steps.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = step.Split(':');
                    if (parts[0].Equals("capture", StringComparison.OrdinalIgnoreCase))
                    {
                        int captureMs = parts.Length > 1 && int.TryParse(parts[1], out int cms) ? cms : 5000;
                        Directory.CreateDirectory(folder);
                        string capturePath = AppSettings.UniquePath(folder, "selftest", ".avi");
                        var started = await _controller.StartCaptureAsync(capturePath);
                        Log.Info($"SELFTEST capture start ok={started.Ok} {started.Message}");
                        if (!started.Ok) continue;
                        SetCapturing(true);
                        await Task.Delay(captureMs);
                        Probe("capturing");
                        var saved = await _controller.StopCaptureAsync();
                        SetCapturing(false);
                        Log.Info($"SELFTEST capture saved ok={saved.Ok} path={saved.Path} bytes={saved.Bytes} elapsed={saved.Elapsed} dropped={saved.DroppedFrames} {saved.Message}");
                        continue;
                    }
                    if (int.TryParse(parts[0], out int raw))
                    {
                        int rawWait = parts.Length > 1 && int.TryParse(parts[1], out int rms) ? rms : 2000;
                        int hr = await _controller.SendRawModeAsync(raw);
                        Log.Info($"SELFTEST raw {raw} hr=0x{hr:X8}");
                        await Task.Delay(Math.Min(800, rawWait));
                        Probe($"raw {raw} +0.8s");
                        await Task.Delay(Math.Max(0, rawWait - 800));
                        Probe($"raw {raw} +{rawWait / 1000.0:0.#}s");
                        continue;
                    }
                    TransportCommand command = parts[0].ToLowerInvariant() switch
                    {
                        "play" => TransportCommand.Play,
                        "pause" => TransportCommand.Pause,
                        "ff" => TransportCommand.FastForward,
                        "rew" => TransportCommand.Rewind,
                        _ => TransportCommand.Stop,
                    };
                    int wait = parts.Length > 1 && int.TryParse(parts[1], out int ms) ? ms : 2000;
                    // Probe twice so a mode that changes, then reverts, is visible.
                    var result = await _controller.SendAsync(command);
                    Log.Info($"SELFTEST {command} ok={result.Ok} {result.Message}");
                    await Task.Delay(Math.Min(800, wait));
                    Probe($"{command} +0.8s");
                    await Task.Delay(Math.Max(0, wait - 800));
                    Probe($"{command} +{wait / 1000.0:0.#}s");
                }
                // Leave the tape the way we found it.
                TransportCommand? back = initial switch
                {
                    TransportMode.Stopped => TransportCommand.Stop,
                    TransportMode.Paused => TransportCommand.Pause,
                    _ => null,
                };
                if (back is TransportCommand b && _mode != initial) await Step(b, 1500, $"restored ({initial})");
                Log.Info("SELFTEST done");
                return;
            }

            await Step(TransportCommand.Play, 5000, "playing");
            await Step(TransportCommand.Pause, 2000, "paused");
            await Step(TransportCommand.Play, 2500, "playing again");

            if (!_captureAvailable)
            {
                Log.Info("SELFTEST capture skipped: capture not available");
            }
            else
            {
                Directory.CreateDirectory(folder);
                string path = AppSettings.UniquePath(folder, "selftest", ".avi");
                var start = await _controller.StartCaptureAsync(path);
                Log.Info($"SELFTEST capture start ok={start.Ok} {start.Message}");
                if (start.Ok)
                {
                    SetCapturing(true);
                    await Task.Delay(5000);
                    Probe("capturing");
                    await Task.Delay(5000);
                    var outcome = await _controller.StopCaptureAsync();
                    SetCapturing(false);
                    Log.Info($"SELFTEST capture saved ok={outcome.Ok} path={outcome.Path} bytes={outcome.Bytes} elapsed={outcome.Elapsed} dropped={outcome.DroppedFrames}");
                    ShowToast($"Self-test capture saved ({FormatBytes(outcome.Bytes)}).", ToastKind.Success);

                    if (outcome.Ok && outcome.Path is string saved)
                    {
                        var cleaned = await PostProcess.CleanAudioAsync(saved, ["Tape Hiss Reducer"]);
                        Log.Info($"SELFTEST clean audio ok={cleaned.Ok} out={cleaned.OutputPath} {cleaned.Message}");
                    }
                }
            }

            TransportCommand? restore = initial switch
            {
                TransportMode.Stopped => TransportCommand.Stop,
                TransportMode.Paused => TransportCommand.Pause,
                TransportMode.Playing => null,
                _ => TransportCommand.Stop,
            };
            if (restore is TransportCommand r) await Step(r, 1500, $"restored ({initial})");
            Log.Info("SELFTEST done");
        }
        catch (Exception ex)
        {
            Log.Error("SELFTEST failed", ex);
        }
    }

    private async Task Step(TransportCommand command, int waitMs, string label)
    {
        var result = await _controller.SendAsync(command);
        Log.Info($"SELFTEST {command} ok={result.Ok} {result.Message}");
        await Task.Delay(waitMs);
        Probe(label);
    }

    private void Probe(string label)
    {
        var frames = _controller.Frames;
        var audio = _controller.Audio;
        long age = frames.MillisecondsSinceLastFrame;
        Log.Info($"SELFTEST [{label}] mode={_mode} picture={(age < 1500 ? $"yes ({age} ms)" : "no")} " +
                 $"frame={frames.Width}x{frames.Height} audio={audio.SampleRate}Hz/{audio.Channels}ch/{audio.BitsPerSample}bit " +
                 $"peak={audio.Peak:0.000} position={TimecodeMain.Text}{TimecodeFrames.Text} signal={SignalWord.Text} " +
                 $"video='{VideoInfo.Text}' audioInfo='{AudioInfo.Text}' format='{FormatText.Text}'");
    }
}
#endif
