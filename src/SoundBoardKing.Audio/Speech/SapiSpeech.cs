using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace SoundBoardKing.Audio.Speech;

/// <summary>
/// The Windows speech engine (SAPI 5): the classic SAPI5 voices, the modern Windows voices (OneCore:
/// Cosimo, Elsa…) and SAPI5 voices installed only for 32-bit programs (such as old Loquendo voices).
/// </summary>
internal static class SapiSpeech
{
    // SAPI's own token categories, as SpObjectTokenCategory.SetId wants them.
    private static readonly string[] Categories =
    [
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices",
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Speech\Voices",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices",
    ];

    private const string Wow64Tokens = @"SOFTWARE\WOW6432Node\Microsoft\Speech\Voices\Tokens";

    // SpeechVoiceSpeakFlags.
    private const int SpeakAsync = 1;
    private const int PurgeBeforeSpeak = 2;
    private const int IsNotXml = 16;
    // SpeechAudioFormatType.SAFT48kHz16BitMono; SpeechStreamFileMode.SSFMCreateForWrite.
    private const int Format48kHz16BitMono = 38;
    private const int CreateForWrite = 3;


    /// <summary>Every voice that can speak, each name once; the 64-bit one wins over its 32-bit twin.</summary>
    public static Task<IReadOnlyList<TtsVoice>> GetVoicesAsync() => RunSta(GetVoices);

    private static IReadOnlyList<TtsVoice> GetVoices()
    {
        var voices = new List<TtsVoice>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in Categories)
        {
            foreach (var voice in TryEnumerate(category))
            {
                if (names.Add(voice.Name))
                    voices.Add(voice);
            }
        }
        foreach (var voice in Enumerate32Bit())
        {
            if (names.Add(voice.Name))
                voices.Add(voice);
        }
        return voices;
    }

    private static List<TtsVoice> TryEnumerate(string categoryId)
    {
        var voices = new List<TtsVoice>();
        try
        {
            dynamic category = CreateCom("SAPI.SpObjectTokenCategory");
            category.SetId(categoryId, false);
            dynamic tokens = category.EnumerateTokens();
            int count = tokens.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic token = tokens.Item(i);
                string name = Attribute(token, "Name") ?? (string)token.GetDescription(0);
                voices.Add(new TtsVoice(name, LanguageName(Attribute(token, "Language")),
                    GenderName(Attribute(token, "Gender")), TtsEngine.Windows, (string)token.Id));
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // The category does not exist on this PC (e.g. no per-user voices).
        }
        return voices;
    }

    private static string? Attribute(dynamic token, string name)
    {
        try
        {
            string value = token.GetAttribute(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    // A 64-bit process does not see these: read them from the registry and speak them from a 32-bit process.
    private static List<TtsVoice> Enumerate32Bit()
    {
        var voices = new List<TtsVoice>();
        using var tokens = Registry.LocalMachine.OpenSubKey(Wow64Tokens);
        if (tokens is null)
            return voices;
        foreach (var id in tokens.GetSubKeyNames())
        {
            using var token = tokens.OpenSubKey(id);
            using var attributes = token?.OpenSubKey("Attributes");
            string? name = attributes?.GetValue("Name") as string ?? token?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            voices.Add(new TtsVoice(name, LanguageName(attributes?.GetValue("Language") as string),
                GenderName(attributes?.GetValue("Gender") as string), TtsEngine.Windows, id, Is32Bit: true));
        }
        return voices;
    }

    /// <summary>SAPI gives the language as hex LCIDs ("410;409"): the first one, by its Italian name.</summary>
    internal static string LanguageName(string? lcids)
    {
        string? first = lcids?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null || !int.TryParse(first, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int lcid))
            return "";
        try
        {
            // In the voice's own language ("italiano (Italia)", "English (United States)"), capitalised.
            string native = CultureInfo.GetCultureInfo(lcid).NativeName;
            return native.Length == 0 ? "" : char.ToUpper(native[0], CultureInfo.InvariantCulture) + native[1..];
        }
        catch (CultureNotFoundException)
        {
            return "";
        }
    }

    internal static string GenderName(string? gender) => gender?.ToLowerInvariant() switch
    {
        "male" => "maschile",
        "female" => "femminile",
        _ => "",
    };

    /// <summary>Speaks <paramref name="text"/> into the wave file <paramref name="path"/>.</summary>
    /// <param name="rate">-10 (slowest) .. 0 (normal) .. 10 (fastest).</param>
    /// <exception cref="InvalidOperationException">The voice could not speak.</exception>
    public static Task SynthesizeAsync(string text, TtsVoice voice, int rate, string path, CancellationToken cancellation)
    {
        rate = Math.Clamp(rate, TextToSpeech.MinRate, TextToSpeech.MaxRate);
        return voice.Is32Bit
            ? Synthesize32BitAsync(text, voice, rate, path, cancellation)
            : RunSta(() => Synthesize(text, voice, rate, path, cancellation));
    }

    private static bool Synthesize(string text, TtsVoice voice, int rate, string path, CancellationToken cancellation)
    {
        dynamic? stream = null;
        try
        {
            dynamic token = CreateCom("SAPI.SpObjectToken");
            token.SetId(voice.Id, "", false);
            dynamic speaker = CreateCom("SAPI.SpVoice");
            speaker.Voice = token;
            speaker.Rate = rate;

            dynamic format = CreateCom("SAPI.SpAudioFormat");
            format.Type = Format48kHz16BitMono;
            stream = CreateCom("SAPI.SpFileStream");
            stream.Format = format;
            stream.Open(path, CreateForWrite, false);
            speaker.AudioOutputStream = stream;

            // Asynchronous, so a long text can be cancelled between waits.
            speaker.Speak(text, SpeakAsync | IsNotXml);
            while (!(bool)speaker.WaitUntilDone(100))
            {
                if (cancellation.IsCancellationRequested)
                {
                    speaker.Speak("", SpeakAsync | PurgeBeforeSpeak);
                    cancellation.ThrowIfCancellationRequested();
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new InvalidOperationException($"La voce \"{voice.Name}\" non funziona: {ex.Message}", ex);
        }
        finally
        {
            try { stream?.Close(); } catch (System.Runtime.InteropServices.COMException) { }
        }
    }

    // The 32-bit Windows PowerShell loads the 32-bit voice; the text travels in a UTF-8 file, never on the command line.
    private static async Task Synthesize32BitAsync(string text, TtsVoice voice, int rate, string path, CancellationToken cancellation)
    {
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell))
            throw new InvalidOperationException("Per le voci a 32 bit serve Windows PowerShell a 32 bit, che non è installato.");

        string textFile = path + ".txt";
        await File.WriteAllTextAsync(textFile, text, new UTF8Encoding(true), cancellation);
        try
        {
            string script = $$"""
                $ErrorActionPreference = 'Stop'
                $text = [IO.File]::ReadAllText('{{Quote(textFile)}}')
                $voice = New-Object -ComObject SAPI.SpVoice
                $tokens = $voice.GetVoices()
                $found = $null
                for ($i = 0; $i -lt $tokens.Count; $i++) {
                    $t = $tokens.Item($i)
                    if ($t.GetAttribute('Name') -eq '{{Quote(voice.Name)}}' -or $t.Id.EndsWith('\{{Quote(voice.Id)}}')) { $found = $t; break }
                }
                if ($null -eq $found) { throw 'voce non trovata' }
                $voice.Voice = $found
                $voice.Rate = {{rate}}
                $format = New-Object -ComObject SAPI.SpAudioFormat
                $format.Type = {{Format48kHz16BitMono}}
                $stream = New-Object -ComObject SAPI.SpFileStream
                $stream.Format = $format
                $stream.Open('{{Quote(path)}}', {{CreateForWrite}}, $false)
                $voice.AudioOutputStream = $stream
                [void]$voice.Speak($text, {{IsNotXml}})
                $stream.Close()
                """;
            var start = new ProcessStartInfo(powershell)
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Impossibile avviare PowerShell a 32 bit.");
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                throw;
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"La voce \"{voice.Name}\" non funziona: {(await errors).Trim()}");
        }
        finally
        {
            TryDelete(textFile);
        }
    }

    private static string Quote(string value) => value.Replace("'", "''");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static object CreateCom(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;

    // SAPI is happiest on an STA thread with nothing else to do, as in a classic desktop program.
    private static Task<T> RunSta<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "SBK text-to-speech",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
