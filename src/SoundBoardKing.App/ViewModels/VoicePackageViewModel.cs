using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoardKing.Audio.Speech;

namespace SoundBoardKing.App.ViewModels;

/// <summary>A row of "Voci neurali": a package to download, downloading, or installed.</summary>
public sealed partial class VoicePackageViewModel(CatalogPackage package, VoiceCatalog catalog) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(SizeText))]
    private bool isInstalled = catalog.IsInstalled(package);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool isDownloading;

    [ObservableProperty]
    private double progress;

    public CatalogPackage Package { get; } = package;
    public string Title => Package.Title;
    public string Credits => Package.Credits;

    public string Speakers => string.Join(", ", Package.Speakers.Select(s => $"{s.Name.Split(' ')[0]} ({s.Gender})"));

    public bool CanDownload => !IsInstalled && !IsDownloading;

    /// <summary>What a download costs now (eSpeak is counted with the first voice only).</summary>
    public string SizeText => IsInstalled ? "Installata" : $"Scarica · {catalog.DownloadSize(Package) / 1_000_000.0:0} MB";

    public void Refresh()
    {
        IsInstalled = catalog.IsInstalled(Package);
        OnPropertyChanged(nameof(SizeText));
    }
}
