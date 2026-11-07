using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SoundBoardKing.App.Services;
using SoundBoardKing.App.ViewModels;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.App.Views;

/// <summary>Stays open beside the main window, so phrases can be said while sounds play.</summary>
public partial class TtsWindow : Window
{
    private readonly TtsViewModel _model;

    public TtsWindow(TtsViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _model = model;
        DataContext = model;
        Loaded += async (_, _) =>
        {
            TextBox.Focus();
            await _model.LoadVoicesAsync();
        };
        Closed += (_, _) => _model.Dispose();
    }

    // Enter speaks (and leaves the text there, to say it again or change it); Shift+Enter goes to a new line.
    private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            int caret = TextBox.CaretIndex;
            TextBox.SelectedText = Environment.NewLine;
            TextBox.CaretIndex = caret + Environment.NewLine.Length;
            TextBox.SelectionLength = 0;
        }
        else if (_model.SpeakCommand.CanExecute(PlayTarget.Default))
        {
            _model.SpeakCommand.Execute(PlayTarget.Default);
            TextBox.SelectAll();
        }
    }

    private async void AddRvc_Click(object sender, RoutedEventArgs e)
    {
        var editor = new RvcVoiceWindow(_model.EditorFor(null)) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } saved)
            await _model.SaveRvcVoiceAsync(null, saved);
    }

    private async void EditRvc_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RvcVoiceItem item })
            return;
        var editor = new RvcVoiceWindow(_model.EditorFor(item)) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } saved)
            await _model.SaveRvcVoiceAsync(item, saved);
    }

    private void RateSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e) => _model.Rate = 0;

    private void EditPhrase_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string phrase })
            return;
        _model.Text = phrase;
        TextBox.Focus();
        TextBox.CaretIndex = phrase.Length;
    }
}
