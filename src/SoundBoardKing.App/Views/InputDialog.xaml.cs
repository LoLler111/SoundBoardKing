using System.Windows;
using SoundBoardKing.App.Services;

namespace SoundBoardKing.App.Views;

public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string initialValue)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
