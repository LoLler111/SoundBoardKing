using System.IO;
using System.Windows;
using Microsoft.Win32;
using SoundBoardKing.App.ViewModels;
using SoundBoardKing.App.Views;
using SoundBoardKing.Audio;

namespace SoundBoardKing.App.Services;

public sealed class DialogService(Window owner) : IDialogService
{
    public void ShowError(string message) =>
        MessageDialog.Show(owner, message, MessageKind.Error);

    public void ShowInfo(string message) =>
        MessageDialog.Show(owner, message);

    public bool Confirm(string message) =>
        MessageDialog.Confirm(owner, message);

    public bool Confirm(string message, string yes, string no) =>
        MessageDialog.Confirm(owner, message, yes: yes, no: no);

    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (initialFolder is not null && Directory.Exists(initialFolder))
            dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    public string? OpenSplFile(string? initialFolder)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importa la lista di SoundPad",
            Filter = "Lista SoundPad (*.spl)|*.spl|Tutti i file (*.*)|*.*",
            InitialDirectory = initialFolder ?? "",
        };
        return FileDialogs.Show(dialog, owner) ? dialog.FileName : null;
    }

    public IReadOnlyList<string> OpenAudioFiles()
    {
        var patterns = string.Join(";", AudioFileProbe.SupportedExtensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = "Aggiungi suoni",
            Filter = $"File audio|{patterns}|Tutti i file (*.*)|*.*",
            Multiselect = true,
        };
        return FileDialogs.Show(dialog, owner) ? dialog.FileNames : [];
    }

    public string? AskText(string title, string prompt, string initialValue = "")
    {
        var dialog = new InputDialog(title, prompt, initialValue) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public string? CaptureHotkey(string title, string? current)
    {
        var dialog = new HotkeyCaptureWindow(title, current) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public bool EditSettings(SettingsDialogModel model)
    {
        var dialog = new SettingsWindow(model) { Owner = owner };
        return dialog.ShowDialog() == true;
    }
}
