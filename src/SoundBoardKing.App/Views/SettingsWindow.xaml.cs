using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SoundBoardKing.Core.Storage;
using SoundBoardKing.App.Services;
using SoundBoardKing.App.ViewModels;

namespace SoundBoardKing.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsDialogModel _model;

    public SettingsWindow(SettingsDialogModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        _model = model;
        DataContext = model;
    }

    private void ChangeStop_Click(object sender, RoutedEventArgs e)
    {
        if (Capture("Tasto rapido: ferma tutto", _model.StopHotkey) is { } hotkey)
            _model.StopHotkey = hotkey.Length == 0 ? null : hotkey;
    }

    private void ChangePause_Click(object sender, RoutedEventArgs e)
    {
        if (Capture("Tasto rapido: pausa / riprendi", _model.PauseHotkey) is { } hotkey)
            _model.PauseHotkey = hotkey.Length == 0 ? null : hotkey;
    }

    private string? Capture(string title, string? current)
    {
        var dialog = new HotkeyCaptureWindow(title, current) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void BrowseSoundsFolder_Click(object sender, RoutedEventArgs e)
    {
        var current = SoundFolder.Resolve(_model.SoundsFolder, _model.AppFolder);
        var dialog = new OpenFolderDialog { Title = "Cartella dei suoni" };
        if (current is not null && Directory.Exists(current))
            dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) == true)
            _model.ChooseSoundsFolder(dialog.FolderName);
    }

    private void MoveNextToExe_Click(object sender, RoutedEventArgs e)
    {
        var target = _model.ExecutableProfilePath;
        if (File.Exists(target) && !Ask($"Accanto all'eseguibile c'è già un profilo:\n{target}\n\nSostituirlo con quello attuale?"))
            return;
        _model.MoveProfile(target);
    }

    private void MoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (PickTarget("Sposta il profilo") is { } path)
            _model.MoveProfile(path);
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (PickTarget("Esporta una copia del profilo") is { } path)
            _model.ExportProfile(path);
    }

    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Apri un profilo",
            Filter = ProfileFilter,
            InitialDirectory = Path.GetDirectoryName(_model.ProfilePath) ?? "",
        };
        if (!FileDialogs.Show(dialog, this))
            return;
        if (string.Equals(Path.GetFullPath(dialog.FileName), _model.ProfilePath, StringComparison.OrdinalIgnoreCase))
            return;
        if (!Ask("SoundBoardKing si riavvia e usa questo profilo, con la sua libreria e le sue impostazioni.\n" +
                 "Il profilo attuale viene salvato e resta dov'è. Continuare?"))
            return;
        if (_model.UseProfile(dialog.FileName))
            DialogResult = false;
    }

    private void ShowProfile_Click(object sender, RoutedEventArgs e)
    {
        var path = _model.ProfilePath;
        var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }

    private const string ProfileFilter = "Profilo SoundBoardKing (*.sbk)|*.sbk|Tutti i file (*.*)|*.*";

    private string? PickTarget(string title)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = ProfileFilter,
            FileName = ProfileStorage.FileName,
            InitialDirectory = Path.GetDirectoryName(_model.AllowsAppFolder ? _model.ExecutableProfilePath : _model.ProfilePath) ?? "",
        };
        return FileDialogs.Show(dialog, this) ? dialog.FileName : null;
    }

    private bool Ask(string message) =>
        MessageDialog.Confirm(this, message);

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    // Keeps the settings changed in the dialog, then the app closes and the updater restarts it.
    private void RestartToUpdate_Click(object sender, RoutedEventArgs e)
    {
        _model.RequestUpdateRestart();
        DialogResult = true;
    }
}
