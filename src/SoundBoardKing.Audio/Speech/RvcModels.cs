using System.IO.Compression;
using SoundBoardKing.Audio.Speech.Rvc;

namespace SoundBoardKing.Audio.Speech;

/// <summary>Checks and unpacks the RVC models the user picks.</summary>
public static class RvcModels
{
    /// <summary>Why the model cannot be used (in Italian, for the user), or null when it can.</summary>
    public static string? Check(string modelPath)
    {
        try
        {
            if (TorchCheckpoint.Load(modelPath) is not Dictionary<object, object?> model || !model.ContainsKey("weight"))
                return "Il file non è un modello RVC.";
            string version = model.GetValueOrDefault("version") as string ?? "v1";
            string rate = model.GetValueOrDefault("sr") as string ?? "?";
            bool pitch = model.GetValueOrDefault("f0") is not long f || f == 1;
            return version == "v2" && rate == "48k" && pitch
                ? null
                : $"Per ora sono supportati solo i modelli RVC v2 a 48 kHz con intonazione (questo è {version}, {rate}{(pitch ? "" : ", senza intonazione")}).";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            return ex.Message;
        }
    }

    /// <summary>The .index next to a model, if there is one (RVC saves it in the same folder).</summary>
    public static string? FindIndex(string modelPath)
    {
        string? folder = Path.GetDirectoryName(modelPath);
        return folder is null ? null : Directory.EnumerateFiles(folder, "*.index").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
    }

    /// <summary>
    /// Unpacks only the .pth and .index of a model archive into <paramref name="folder"/>; returns the model
    /// and the index (null when the archive has none).
    /// </summary>
    /// <exception cref="InvalidDataException">The archive has no .pth.</exception>
    public static (string Model, string? Index) Unpack(string zipPath, string folder)
    {
        Directory.CreateDirectory(folder);
        string? model = null, index = null;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            string extension = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (extension is not (".pth" or ".index") || entry.Name.Length == 0)
                continue;
            // Only the file name: whatever folders the archive has, nothing lands outside the target.
            string target = Path.Combine(folder, entry.Name);
            entry.ExtractToFile(target, overwrite: true);
            if (extension == ".pth" && (model is null || entry.Length > new FileInfo(model).Length))
                model = target;
            else if (extension == ".index" && (index is null || entry.Length > new FileInfo(index).Length))
                index = target;
        }
        return (model ?? throw new InvalidDataException("Nell'archivio non c'è un modello RVC (.pth)."), index);
    }
}
