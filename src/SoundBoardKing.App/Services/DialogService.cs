using System.Windows;
using Microsoft.Win32;
using SoundBoardKing.App.ViewModels;
using SoundBoardKing.App.Views;
using SoundBoardKing.Audio;

namespace SoundBoardKing.App.Services;

public sealed class DialogService(Window owner) : IDialogService
{
    private const string Caption = "SoundBoardKing";

    public void ShowError(string message) =>
        MessageBox.Show(owner, message, Caption, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string message) =>
        MessageBox.Show(owner, message, Caption, MessageBoxButton.OK, MessageBoxImage.Information);

    public bool Confirm(string message) =>
        MessageBox.Show(owner, message, Caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? OpenSplFile(string? initialFolder)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importa la lista di SoundPad",
            Filter = "Lista SoundPad (*.spl)|*.spl|Tutti i file (*.*)|*.*",
            InitialDirectory = initialFolder ?? "",
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
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
        return dialog.ShowDialog(owner) == true ? dialog.FileNames : [];
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
