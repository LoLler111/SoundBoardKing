namespace SoundBoardKing.Audio.Speech;

/// <summary>What makes a voice speak.</summary>
public enum TtsEngine
{
    /// <summary>The speech engine of Windows (SAPI 5): its own voices and those of other makers, such as Loquendo.</summary>
    Windows,

    /// <summary>Piper: small neural voices that run on the PC, downloaded on request.</summary>
    Piper,

    /// <summary>Kokoro: a neural model with many voices that runs on the PC, downloaded on request.</summary>
    Kokoro,

    /// <summary>
    /// RVC: another voice (the base) says the text, then a voice-conversion model the user chose turns it
    /// into the voice it was trained on.
    /// </summary>
    Rvc,
}

/// <summary>A voice that can say a text.</summary>
/// <param name="Name">Shown in the list; also how the choice is remembered, so unique across engines.</param>
/// <param name="Id">Windows: the SAPI token. Piper and Kokoro: the catalog id of the voice.</param>
/// <param name="Is32Bit">
/// Windows only: installed for 32-bit programs only (older SAPI5 voices), so it speaks through the 32-bit
/// Windows PowerShell.
/// </param>
public sealed record TtsVoice(string Name, string Language, string Gender, TtsEngine Engine, string Id, bool Is32Bit = false)
{
    public string Label => Language.Length == 0 ? Name : $"{Name} · {Language}";
}
