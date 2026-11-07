using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SoundBoardKing.Audio.Speech;

/// <summary>
/// Turns text into IPA phonemes with eSpeak NG, as Piper and Kokoro expect. The library is downloaded
/// with the first neural voice (it is not part of the app) and loaded from that folder.
/// </summary>
internal sealed partial class ESpeak : IDisposable
{
    // espeak_AUDIO_OUTPUT.AUDIO_OUTPUT_SYNCHRONOUS; espeakCHARS_UTF8; espeakPHONEMES_IPA.
    private const int OutputSynchronous = 2;
    private const int CharsUtf8 = 1;
    private const int PhonemesIpa = 0x02;

    // How eSpeak ends a clause (translate.h): the punctuation is told apart by pause, intonation and type.
    private const int ClauseMask = 0x000FFFFF;
    private const int TypeSentence = 0x00080000;
    private const int Period = 40 | 0x00000000 | TypeSentence;
    private const int Question = 40 | 0x00002000 | TypeSentence;
    private const int Exclamation = 45 | 0x00003000 | TypeSentence;
    private const int Comma = 20 | 0x00001000 | 0x00040000;
    private const int Colon = 30 | 0x00000000 | 0x00040000;
    private const int Semicolon = 30 | 0x00001000 | 0x00040000;

    // eSpeak keeps one global state for the whole process: it is loaded once, shared by every instance,
    // and shut down only when the last one is disposed — never under another instance's feet.
    private static readonly Lock Gate = new();
    private static int _users;
    private static nint _library;
    private static unsafe delegate* unmanaged[Cdecl]<byte*, int> _setVoiceByName;
    private static unsafe delegate* unmanaged[Cdecl]<void**, int, int, int*, byte*> _toPhonemesWithTerminator;
    private static unsafe delegate* unmanaged[Cdecl]<void**, int, int, byte*> _toPhonemes;
    private static unsafe delegate* unmanaged[Cdecl]<int> _terminate;
    private static string? _voice;
    private bool _disposed;

    /// <param name="folder">The folder with espeak-ng.dll and espeak-ng-data (used by the first instance).</param>
    /// <exception cref="InvalidOperationException">The library is missing or does not start.</exception>
    public ESpeak(string folder)
    {
        lock (Gate)
        {
            if (_users == 0)
                Load(folder);
            _users++;
        }
    }

    private static unsafe void Load(string folder)
    {
        string dll = Path.Combine(folder, "espeak-ng.dll");
        if (!File.Exists(dll))
            throw new InvalidOperationException("Il motore delle voci neurali (eSpeak NG) non è installato.");
        nint library = NativeLibrary.Load(dll);
        try
        {
            var initialize = (delegate* unmanaged[Cdecl]<int, int, byte*, int, int>)NativeLibrary.GetExport(library, "espeak_Initialize");
            _setVoiceByName = (delegate* unmanaged[Cdecl]<byte*, int>)NativeLibrary.GetExport(library, "espeak_SetVoiceByName");
            _toPhonemes = (delegate* unmanaged[Cdecl]<void**, int, int, byte*>)NativeLibrary.GetExport(library, "espeak_TextToPhonemes");
            _terminate = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(library, "espeak_Terminate");
            // Only in Piper's build of eSpeak: it also tells which punctuation ended each clause.
            _toPhonemesWithTerminator = NativeLibrary.TryGetExport(library, "espeak_TextToPhonemesWithTerminator", out var withTerminator)
                ? (delegate* unmanaged[Cdecl]<void**, int, int, int*, byte*>)withTerminator
                : null;

            byte[] data = Encoding.UTF8.GetBytes(Path.Combine(folder, "espeak-ng-data") + "\0");
            int rate;
            fixed (byte* path = data)
                rate = initialize(OutputSynchronous, 0, path, 0);
            if (rate <= 0)
                throw new InvalidOperationException("eSpeak NG non si avvia: i suoi dati sono danneggiati o mancano.");
            _library = library;
            _voice = null;
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    /// <summary>
    /// The phonemes of <paramref name="text"/>, one string per sentence, with the punctuation that shapes
    /// the intonation (, . ? ! : ;) kept as the voices were trained. Every line is at least a sentence.
    /// </summary>
    /// <param name="language">eSpeak voice, e.g. "it" or "en-us".</param>
    public List<string> Phonemize(string text, string language)
    {
        var sentences = new List<string>();
        lock (Gate)
        {
            SetVoice(language);
            foreach (var line in Lines(text))
                PhonemizeLine(line, sentences);
        }
        return sentences;
    }

    private unsafe void PhonemizeLine(string line, List<string> sentences)
    {
        var current = new StringBuilder();
        byte[] utf8 = Encoding.UTF8.GetBytes(line + "\0");
        fixed (byte* start = utf8)
        {
            void* position = start;
            while (position != null)
            {
                int terminator = 0;
                byte* phonemes = _toPhonemesWithTerminator != null
                    ? _toPhonemesWithTerminator(&position, CharsUtf8, PhonemesIpa, &terminator)
                    : _toPhonemes(&position, CharsUtf8, PhonemesIpa);
                if (phonemes != null)
                    current.Append(Marshal.PtrToStringUTF8((nint)phonemes));

                switch (terminator & ClauseMask)
                {
                    case Period: current.Append('.'); break;
                    case Question: current.Append('?'); break;
                    case Exclamation: current.Append('!'); break;
                    case Comma: current.Append(", "); break;
                    case Colon: current.Append(": "); break;
                    case Semicolon: current.Append("; "); break;
                    default:
                        // Without the terminators every clause just follows the last one.
                        if (_toPhonemesWithTerminator == null)
                            current.Append(' ');
                        break;
                }
                if ((terminator & TypeSentence) == TypeSentence)
                    EndSentence();
            }
        }
        // A line without final punctuation still ends like a sentence.
        if (current.ToString().TrimEnd() is { Length: > 0 } rest && rest[^1] is not ('.' or '?' or '!'))
            current.Append('.');
        EndSentence();

        void EndSentence()
        {
            // eSpeak marks a switch of language like "(en)": the voice cannot say that.
            string sentence = Whitespace().Replace(LanguageSwitch().Replace(current.ToString(), ""), " ").Trim();
            if (sentence.Length > 0)
                sentences.Add(sentence);
            current.Clear();
        }
    }

    private unsafe void SetVoice(string language)
    {
        if (_voice == language)
            return;
        byte[] name = Encoding.UTF8.GetBytes(language + "\0");
        fixed (byte* pointer = name)
        {
            if (_setVoiceByName(pointer) != 0)
                throw new InvalidOperationException($"eSpeak NG non conosce la lingua \"{language}\".");
        }
        _voice = language;
    }

    // Typographic apostrophes eSpeak would read as symbols.
    private static IEnumerable<string> Lines(string text) =>
        text.Replace('’', '\'').Replace('‘', '\'')
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"\([a-z]{2,3}(-[a-z0-9]+)*\)")]
    private static partial Regex LanguageSwitch();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public unsafe void Dispose()
    {
        lock (Gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (--_users > 0)
                return;
            _terminate();
            NativeLibrary.Free(_library);
            _library = 0;
            _voice = null;
        }
    }
}
