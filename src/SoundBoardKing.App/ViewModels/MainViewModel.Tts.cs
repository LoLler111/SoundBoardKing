using System.IO;
using System.Runtime.InteropServices;
using SoundBoardKing.Audio;
using SoundBoardKing.Audio.Speech;
using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.App.ViewModels;

/// <summary>Marks a playback as spoken text, so the playback bar can show what is being said.</summary>
public sealed record SpokenTextTag(string Title);

// Text to speech: the text becomes a wave file and plays like a sound, through the same effects.
public sealed partial class MainViewModel
{
    private static string TtsFolder => Path.Combine(Path.GetTempPath(), "SoundBoardKing", "Voce");

    // The neural voices are big: kept in the local app data, beside the voice isolation model, not next to the app.
    private readonly TextToSpeech _speech = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundBoardKing", "Voci"));

    /// <summary>The voices of Windows and the neural voices, with the catalog to download them.</summary>
    public TextToSpeech Speech
    {
        get
        {
            ApplyRvcVoices();
            return _speech;
        }
    }

    /// <summary>Where the RVC models picked from a .zip are unpacked.</summary>
    public static string RvcFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundBoardKing", "Voci", "rvc");

    /// <summary>Hands the RVC voices and the graphics card choice of this PC's settings to the speech engine.</summary>
    public void ApplyRvcVoices()
    {
        _speech.RvcVoices = _storage.Local.RvcVoices
            .Select(v => new RvcVoiceDefinition(v.Name, v.ModelPath, v.IndexPath, v.BaseVoice, v.Pitch, v.IndexRate, v.Intonation))
            .ToList();
        _speech.UseGpu = _storage.Local.TtsUseGpu;
    }

    public LocalSettings Local => _storage.Local;

    public void SaveLocalSettings() => _storage.SaveLocal();

    public bool IsMicAvailable => _engine.IsMicAvailable;

    /// <summary>
    /// Speaks <paramref name="text"/> with <paramref name="voice"/>: in the headphones and, like the sounds,
    /// in the microphone when that is on. Returns why it could not, or null.
    /// </summary>
    public async Task<string?> SpeakAsync(string text, TtsVoice voice, int rate, PlayTarget target, CancellationToken cancellation)
    {
        if (target == PlayTarget.Mic && !_engine.IsMicAvailable)
            return StatusText = "Microfono non disponibile: installa il modulo dalle impostazioni.";

        ApplyRvcVoices();
        string path = await _speech.SynthesizeAsync(text, voice, rate, TtsFolder, cancellation);
        // Same loudness as the sounds, when they are leveled.
        var loudness = await Task.Run(() => Loudness.TryMeasure(path), cancellation);
        try
        {
            _engine.Play(path, new PlayRequest(
                new SpokenTextTag("🗣 " + OneLine(text)),
                Speakers: target != PlayTarget.Mic,
                Mic: target switch
                {
                    PlayTarget.Speakers => false,
                    PlayTarget.Mic => true,
                    _ => _settings.PlayOnMic && _engine.IsMicAvailable,
                },
                AllowOverlap: _settings.AllowOverlap,
                LevelGain: Loudness.GainFor(loudness?.Lufs, loudness?.PeakDb)));
            return null;
        }
        catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException)
        {
            return StatusText = $"Impossibile riprodurre la voce: {ex.Message}";
        }
    }

    private static string OneLine(string text)
    {
        string line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 60 ? line : line[..57] + "…";
    }
}
