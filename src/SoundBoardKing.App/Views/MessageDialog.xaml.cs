using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundBoardKing.App.Views;

public enum MessageKind
{
    Info,
    Warning,
    Error,
    Question,
}

/// <summary>The app's own message box, in the dark theme instead of the Windows default.</summary>
public partial class MessageDialog : Window
{
    private MessageDialog(string title, string message, MessageKind kind, string yesText, string? noText)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        YesButton.Content = yesText;
        if (noText is null)
        {
            // A single button closes the dialog on Esc as well as Enter.
            NoButton.Visibility = Visibility.Collapsed;
            YesButton.IsCancel = true;
        }
        else
        {
            NoButton.Content = noText;
        }

        var (glyph, color) = kind switch
        {
            MessageKind.Error => ("", "DangerBrush"),
            MessageKind.Warning => ("", "AccentBrush"),
            MessageKind.Question => ("", "AccentBrush"),
            _ => ("", "AccentBrush"),
        };
        var brush = (Brush)FindResource(color);
        IconGlyph.Text = glyph;
        IconGlyph.Foreground = brush;
        IconBack.Fill = brush;
        Title = title;
    }

    /// <summary>Shows a message with an OK button.</summary>
    public static void Show(Window? owner, string message, MessageKind kind = MessageKind.Info, string? title = null) =>
        Open(owner, title ?? DefaultTitle(kind), message, kind, "OK", null);

    /// <summary>Asks a yes/no question; true when the user answers yes.</summary>
    public static bool Confirm(Window? owner, string message, string? title = null, string yes = "Sì", string no = "No") =>
        Open(owner, title ?? "Conferma", message, MessageKind.Question, yes, no);

    private static bool Open(Window? owner, string title, string message, MessageKind kind, string yes, string? no)
    {
        var dialog = new MessageDialog(title, message, kind, yes, no);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
        }
        return dialog.ShowDialog() == true;
    }

    private static string DefaultTitle(MessageKind kind) => kind switch
    {
        MessageKind.Error => "Si è verificato un problema",
        MessageKind.Warning => "Attenzione",
        _ => "SoundBoardKing",
    };

    private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
