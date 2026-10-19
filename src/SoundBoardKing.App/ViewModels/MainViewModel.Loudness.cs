using System.IO;
using SoundBoardKing.Audio;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.App.ViewModels;

public sealed partial class MainViewModel
{
    // Files decoded together, and how often the progress shows; a big import is worth reporting.
    private const int LoudnessBatch = 16;
    private const int LoudnessReportFrom = 20;

    private readonly CancellationTokenSource _loudnessCancel = new();
    private bool _measuringLoudness;
    private bool _measureLoudnessAgain;

    /// <summary>Gain that levels this sound; 1 until its loudness has been measured.</summary>
    private static float LevelGainOf(Sound sound) => Loudness.GainFor(sound.Loudness, sound.Peak);

    /// <summary>Measures, in the background, every sound with no loudness yet; cheap to call after any addition.</summary>
    private async void MeasureLoudness()
    {
        if (_measuringLoudness)
        {
            _measureLoudnessAgain = true;
            return;
        }
        _measuringLoudness = true;
        try
        {
            do
            {
                _measureLoudnessAgain = false;
                await MeasurePendingLoudnessAsync(_loudnessCancel.Token);
            }
            while (_measureLoudnessAgain);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _measuringLoudness = false;
        }
    }

    private async Task MeasurePendingLoudnessAsync(CancellationToken cancel)
    {
        var candidates = _library.Sounds.Where(s => s.Loudness is null).ToList();
        if (candidates.Count == 0)
            return;
        // Missing files are skipped: they get measured at a later start, once they are back.
        var pending = await Task.Run(() => candidates.Where(s => File.Exists(s.Path)).ToList(), cancel);
        bool report = pending.Count >= LoudnessReportFrom;
        int done = 0;
        int measured = 0;

        foreach (var batch in pending.Chunk(LoudnessBatch))
        {
            var paths = batch.Select(s => s.Path).ToArray();
            var results = await Task.Run(() =>
            {
                var found = new LoudnessInfo?[paths.Length];
                var options = new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancel };
                Parallel.For(0, paths.Length, options, i => found[i] = Loudness.TryMeasure(paths[i]));
                return found;
            }, cancel);

            for (int i = 0; i < batch.Length; i++)
            {
                if (results[i] is not { } info)
                    continue;
                batch[i].Loudness = info.Lufs;
                batch[i].Peak = info.PeakDb;
                measured++;
            }
            done += batch.Length;
            if (report)
                StatusText = $"Livellamento del volume: {done}/{pending.Count} suoni analizzati…";
            ScheduleSave();
        }

        if (report)
            StatusText = $"Volume livellato per {measured} suoni.";
    }
}
