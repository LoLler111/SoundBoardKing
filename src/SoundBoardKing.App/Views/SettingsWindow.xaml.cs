using System.Windows;
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

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
