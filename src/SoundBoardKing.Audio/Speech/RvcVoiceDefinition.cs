namespace SoundBoardKing.Audio.Speech;

/// <summary>An RVC voice: a base voice says the text, the user's RVC model changes it into its own.</summary>
/// <param name="Name">Shown in the list of voices, e.g. "Roberto (RVC)".</param>
/// <param name="ModelPath">The user's .pth, left where they keep it.</param>
/// <param name="IndexPath">The model's .index (optional: without it the voice resembles the model less).</param>
/// <param name="BaseVoice">Name of the voice that says the text first.</param>
/// <param name="Pitch">Semitones added to the base voice's pitch.</param>
/// <param name="IndexRate">How much of each frame comes from the index, 0..1.</param>
/// <param name="Intonation">How much the pitch moves around its average: 1 as the base voice, 0 flat, 2 twice as much.</param>
public sealed record RvcVoiceDefinition(string Name, string ModelPath, string? IndexPath, string BaseVoice, int Pitch = 0,
    float IndexRate = 0.66f, float Intonation = 1f);
