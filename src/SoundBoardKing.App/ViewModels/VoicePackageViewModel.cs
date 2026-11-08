using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoardKing.Audio.Speech;

namespace SoundBoardKing.App.ViewModels;

/// <summary>A row of "Voci neurali": a package to download, downloading, or installed.</summary>
public sealed partial class VoicePackageViewModel(CatalogPackage package, VoiceCatalog catalog) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(SizeText), nameof(ShowInstalled), nameof(DownloadTip))]
    private bool isInstalled = catalog.IsInstalled(package);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(SizeText), nameof(ShowInstalled), nameof(DownloadTip))]
    private bool hasUpgrade = catalog.HasUpgrade(package);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(ShowInstalled))]
    private bool isDownloading;

    [ObservableProperty]
    private double progress;

    public CatalogPackage Package { get; } = package;
    public string Title => Package.Title;
    public string Credits => Package.Credits;

    public string Speakers => string.Join(", ", Package.Speakers.Select(s => $"{s.Name.Split(' ')[0]} ({s.Gender})"));

    /// <summary>Not installed, or installed with an older file a better one replaces.</summary>
    public bool CanDownload => (!IsInstalled || HasUpgrade) && !IsDownloading;

    public bool ShowInstalled => IsInstalled && !HasUpgrade && !IsDownloading;

    /// <summary>What a download costs now (eSpeak is counted with the first voice only).</summary>
    public string SizeText => IsInstalled && !HasUpgrade ? "Installata"
        : $"{(HasUpgrade ? "Migliora" : "Scarica")} · {catalog.DownloadSize(Package) / 1_000_000.0:0} MB";

    public string DownloadTip => HasUpgrade
        ? "C'è una versione migliore del modello: più naturale e più veloce, anche sulla scheda video. Intanto la voce funziona con quella che hai"
        : "Scarica la voce (una volta sola)";

    public void Refresh()
    {
        IsInstalled = catalog.IsInstalled(Package);
        HasUpgrade = catalog.HasUpgrade(Package);
        OnPropertyChanged(nameof(SizeText));
    }
}
