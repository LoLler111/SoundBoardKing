namespace SoundBoardKing.Audio.Speech;

/// <summary>
/// Notices a crash on the graphics card: a driver or DirectML fault inside ONNX Runtime is an access violation,
/// which .NET cannot catch, so the app just closes. A file is written before the work on the card and deleted
/// after; found at the next start, it means the app died there.
/// </summary>
internal sealed class GpuCrashGuard(string folder)
{
    private string Marker => Path.Combine(folder, "gpu-in-use");

    /// <summary>Marks the work on the card as started, until the returned object is disposed.</summary>
    public IDisposable Enter()
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Marker, DateTime.Now.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the marker a crash is just not noticed.
        }
        return new Exit(Marker);
    }

    /// <summary>Whether the app closed while working on the card; the mark is then cleared.</summary>
    public bool TakeCrash()
    {
        if (!File.Exists(Marker))
            return false;
        Exit.Delete(Marker);
        return true;
    }

    private sealed class Exit(string marker) : IDisposable
    {
        public void Dispose() => Delete(marker);

        public static void Delete(string marker)
        {
            try { File.Delete(marker); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
