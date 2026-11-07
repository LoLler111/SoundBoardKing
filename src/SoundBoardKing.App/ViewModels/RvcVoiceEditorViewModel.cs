using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoardKing.Audio.Speech;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.App.ViewModels;

/// <summary>The "Voce RVC" dialog: a model from the PC, the voice that speaks before it, pitch and index.</summary>
public sealed partial class RvcVoiceEditorViewModel : ObservableObject
{
    private readonly IReadOnlyCollection<string> _takenNames;

    [ObservableProperty]
    private string name = "";

    [ObservableProperty]
    private string modelPath = "";

    [ObservableProperty]
    private string? indexPath;

    [ObservableProperty]
    private string? baseVoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PitchText))]
    private double pitch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndexText))]
    private double indexPercent = 66;

    [ObservableProperty]
    private string error = "";

    [ObservableProperty]
    private bool isBusy;

    /// <param name="baseVoices">Voices that can say the text first (not RVC voices).</param>
    /// <param name="takenNames">Names of the other voices: the new one must differ.</param>
    public RvcVoiceEditorViewModel(IReadOnlyList<string> baseVoices, IReadOnlyCollection<string> takenNames, RvcVoiceSetting? existing)
    {
        BaseVoices = baseVoices;
        _takenNames = takenNames;
        IsNew = existing is null;
        if (existing is not null)
        {
            name = existing.Name;
            modelPath = existing.ModelPath;
            indexPath = existing.IndexPath;
            baseVoice = existing.BaseVoice;
            pitch = existing.Pitch;
            indexPercent = existing.IndexRate * 100;
        }
        else
        {
            // A male Italian neural voice is the best start for most models; else any Italian voice.
            baseVoice = baseVoices.FirstOrDefault(v => v.StartsWith("Nicola", StringComparison.Ordinal))
                ?? baseVoices.FirstOrDefault(v => v.Contains("Cosimo", StringComparison.Ordinal))
                ?? baseVoices.FirstOrDefault();
        }
    }

    public bool IsNew { get; }
    public string Title => IsNew ? "Aggiungi una voce RVC" : "Modifica la voce RVC";
    public IReadOnlyList<string> BaseVoices { get; }
    public string PitchText => Pitch == 0 ? "come la voce di base" : $"{Pitch:+0;-0} semitoni";
    public string IndexText => $"{IndexPercent:0}%";

    /// <summary>A model picked from the PC: a .pth (its .index is looked for beside it) or a .zip with both.</summary>
    public async Task PickModelAsync(string path)
    {
        Error = "";
        IsBusy = true;
        try
        {
            string model = path;
            string? index = null;
            string suggested = Path.GetFileNameWithoutExtension(path);
            if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Unpacked once into the app's folder: the archive can then be deleted.
                string folder = Path.Combine(MainViewModel.RvcFolder, SafeFolderName(suggested));
                (model, index) = await Task.Run(() => RvcModels.Unpack(path, folder));
            }
            else
            {
                index = RvcModels.FindIndex(path);
            }
            if (await Task.Run(() => RvcModels.Check(model)) is { } problem)
            {
                Error = problem;
                return;
            }
            ModelPath = model;
            IndexPath = index;
            if (string.IsNullOrWhiteSpace(Name))
                Name = $"{char.ToUpperInvariant(suggested[0])}{suggested[1..]} (RVC)";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Error = $"Non riesco a usare il file: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The voice to save, or null with <see cref="Error"/> set.</summary>
    public RvcVoiceSetting? Validate()
    {
        string trimmed = Name.Trim();
        if (string.IsNullOrEmpty(ModelPath) || !File.Exists(ModelPath))
            Error = "Scegli il modello RVC (.pth o .zip).";
        else if (trimmed.Length == 0)
            Error = "Dai un nome alla voce.";
        else if (_takenNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            Error = "C'è già una voce con questo nome.";
        else if (string.IsNullOrEmpty(BaseVoice))
            Error = "Scegli la voce che legge il testo.";
        else
            return new RvcVoiceSetting
            {
                Name = trimmed,
                ModelPath = ModelPath,
                IndexPath = string.IsNullOrEmpty(IndexPath) ? null : IndexPath,
                BaseVoice = BaseVoice,
                Pitch = (int)Math.Round(Pitch),
                IndexRate = (float)Math.Round(IndexPercent / 100, 2),
            };
        return null;
    }

    private static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe.Length == 0 ? "modello" : safe;
    }
}
