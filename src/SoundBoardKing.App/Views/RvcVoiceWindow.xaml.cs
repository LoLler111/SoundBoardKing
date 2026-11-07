using System.Windows;
using Microsoft.Win32;
using SoundBoardKing.App.Services;
using SoundBoardKing.App.ViewModels;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.App.Views;

/// <summary>Adds or changes an RVC voice; <see cref="Result"/> is set when saved.</summary>
public partial class RvcVoiceWindow : Window
{
    private readonly RvcVoiceEditorViewModel _model;

    public RvcVoiceWindow(RvcVoiceEditorViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _model = model;
        DataContext = model;
    }

    public RvcVoiceSetting? Result { get; private set; }

    private async void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Scegli il modello RVC",
            Filter = "Modelli RVC (*.pth, *.zip)|*.pth;*.zip|Tutti i file|*.*",
        };
        if (FileDialogs.Show(dialog, this))
            await _model.PickModelAsync(dialog.FileName);
    }

    private void BrowseIndex_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Scegli l'indice del modello", Filter = "Indici RVC (*.index)|*.index" };
        if (FileDialogs.Show(dialog, this))
            _model.IndexPath = dialog.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_model.IsBusy)
            return;
        Result = _model.Validate();
        if (Result is not null)
            DialogResult = true;
    }
}
