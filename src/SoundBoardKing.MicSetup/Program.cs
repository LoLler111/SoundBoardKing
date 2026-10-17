// Installs or removes the SbkMicFx APO on a microphone. Runs elevated (see app.manifest).
//
//   SoundBoardKing.MicSetup install --device <id> [--slot auto|lfx|gfx|sfx|mfx|efx] [--dll <path>] [--result <file>]
//   SoundBoardKing.MicSetup uninstall (--device <id> | --all) [--result <file>]
//   SoundBoardKing.MicSetup status
//
// <id> is the endpoint id, e.g. {0.0.1.00000000}.{a1b2...}. --result receives "OK" or
// "ERROR" on the first line followed by the log, for the app that launched us via UAC.

using SoundBoardKing.MicSetup;

var options = ParseOptions(args.Skip(1));
var command = args.FirstOrDefault()?.ToLowerInvariant();
var logLines = new List<string>();

void Log(string message)
{
    logLines.Add(message);
    Console.WriteLine(message);
    try
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SoundBoardKing", "micsetup.log");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
    }
    catch (IOException)
    {
    }
}

int exitCode;
try
{
    var installer = new MicInstaller(Log);
    switch (command)
    {
        case "install":
            var device = Required(options, "device");
            var slot = Enum.Parse<FxSlot>(options.GetValueOrDefault("slot", "auto"), ignoreCase: true);
            var dll = options.GetValueOrDefault("dll") ?? Path.Combine(AppContext.BaseDirectory, "SbkMicFx.dll");
            installer.Install(device, slot, dll);
            break;
        case "uninstall":
            installer.Uninstall(options.ContainsKey("all") ? null : Required(options, "device"));
            break;
        case "status":
            var devices = installer.InstalledDevices().ToList();
            Log(devices.Count == 0 ? "Non installato." : "Installato su: " + string.Join(", ", devices));
            break;
        default:
            Log("Uso: install --device <id> [--slot efx|sfx|mfx] | uninstall (--device <id> | --all) | status");
            break;
    }
    exitCode = 0;
}
catch (Exception ex)
{
    Log($"ERRORE: {ex.Message}");
    exitCode = 1;
}

if (options.TryGetValue("result", out var resultFile))
    File.WriteAllLines(resultFile, [exitCode == 0 ? "OK" : "ERROR", .. logLines]);
return exitCode;

static Dictionary<string, string> ParseOptions(IEnumerable<string> arguments)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? pending = null;
    foreach (var argument in arguments)
    {
        if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            pending = argument[2..];
            result[pending] = "";
        }
        else if (pending is not null)
        {
            result[pending] = argument;
            pending = null;
        }
    }
    return result;
}

static string Required(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) && value.Length > 0
        ? value
        : throw new ArgumentException($"Manca --{name}.");
