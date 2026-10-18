using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SoundBoardKing.App.Views;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        WaitForPreviousInstance(e.Args);
        _singleInstance = new Mutex(true, @"Local\SoundBoardKing.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageDialog.Show(null, "SoundBoardKing è già aperto: lo trovi nella barra delle applicazioni.");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
        new MainWindow().Show();
    }

    // "--after <pid>": restarted to open another profile, so let the old instance finish saving and exit.
    private static void WaitForPreviousInstance(string[] args)
    {
        int index = Array.IndexOf(args, "--after");
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int pid))
            return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(TimeSpan.FromSeconds(15));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var log = AppPaths.Default.ErrorLogFile;
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }

        MessageDialog.Show(Current?.MainWindow, $"Errore imprevisto: {e.Exception.Message}", MessageKind.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
