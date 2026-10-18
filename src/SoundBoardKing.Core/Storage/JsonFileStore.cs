using System.Text.Json;

namespace SoundBoardKing.Core.Storage;

/// <summary>Loads and saves one object as a JSON file, writing atomically.</summary>
public sealed class JsonFileStore<T>(string filePath) where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string FilePath { get; } = filePath;

    /// <summary>
    /// Returns a new <typeparamref name="T"/> when the file does not exist. A corrupt file is moved
    /// aside (so the next save cannot overwrite it) and reported with <see cref="InvalidDataException"/>;
    /// with <paramref name="quarantineCorrupt"/> false it is only reported.
    /// </summary>
    public T Load(bool quarantineCorrupt = true)
    {
        if (!File.Exists(FilePath))
            return new T();

        try
        {
            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize<T>(stream, Options) ?? new T();
        }
        catch (JsonException ex) when (!quarantineCorrupt)
        {
            throw new InvalidDataException($"Il file {Path.GetFileName(FilePath)} è danneggiato.", ex);
        }
        catch (JsonException ex)
        {
            var backup = $"{FilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(FilePath, backup);
            throw new InvalidDataException(
                $"Il file {Path.GetFileName(FilePath)} era danneggiato ed è stato spostato in {backup}.", ex);
        }
    }

    public void Save(T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temp = FilePath + ".tmp";
        using (var stream = File.Create(temp))
            JsonSerializer.Serialize(stream, value, Options);
        File.Move(temp, FilePath, overwrite: true);
    }
}
