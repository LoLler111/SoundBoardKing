using System.Windows;
using System.Windows.Input;
using SoundBoardKing.App.Hotkeys;
using SoundBoardKing.App.Services;

namespace SoundBoardKing.App.Views;

public partial class HotkeyCaptureWindow : Window
{
    private HotkeyGesture? _gesture;

    public HotkeyCaptureWindow(string title, string? current)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        TitleText.Text = title;
        if (HotkeyGesture.TryParse(current, out var gesture))
        {
            _gesture = gesture;
            GestureText.Text = gesture.ToString();
        }
        OkButton.IsEnabled = _gesture is not null;
    }

    /// <summary>The captured combination, or an empty string when the user chose "Rimuovi".</summary>
    public string? Result { get; private set; }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Every key is part of the combination, so none may reach the buttons.
        e.Handled = true;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        var modifiers = Keyboard.Modifiers;

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            DialogResult = false;
            return;
        }
        if (HotkeyGesture.IsModifierKey(key))
        {
            GestureText.Text = HotkeyGesture.FormatModifiers(modifiers) + "+…";
            return;
        }

        _gesture = new HotkeyGesture(modifiers, key);
        GestureText.Text = _gesture.Value.ToString();
        OkButton.IsEnabled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = _gesture?.ToString();
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Result = "";
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
