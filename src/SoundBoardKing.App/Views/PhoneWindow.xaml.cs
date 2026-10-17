using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using SoundBoardKing.App.Remote;
using SoundBoardKing.App.Services;
using SoundBoardKing.Core.Storage;
using SoundBoardKing.Remote;

namespace SoundBoardKing.App.Views;

/// <summary>Pairing QR/code and the list of paired phones. A pairing code exists only while this window is open.</summary>
public partial class PhoneWindow : Window
{
    private readonly RemoteHost _remote;

    public PhoneWindow(RemoteHost remote)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _remote = remote;
        _remote.StatusChanged += OnStatusChanged;
        _remote.Devices.Changed += OnDevicesChanged;
        Loaded += (_, _) => Refresh(newCode: true);
        Closed += OnClosed;
    }

    private async void EnabledBox_Click(object sender, RoutedEventArgs e)
    {
        EnabledBox.IsEnabled = false;
        await _remote.SetEnabledAsync(EnabledBox.IsChecked == true);
        EnabledBox.IsEnabled = true;
        Refresh(newCode: true);
    }

    private void OnStatusChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() => Refresh(newCode: false));

    private void OnDevicesChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() => Refresh(newCode: false));

    private void OnDevicePaired(object? sender, PairedDevice device) => Dispatcher.InvokeAsync(() =>
    {
        PairedMessage.Text = $"Abbinato: {device.Name}. Puoi abbinare un altro telefono con il nuovo codice.";
        Refresh(newCode: true);
    });

    private RemoteServer? _subscribedServer;

    private void Refresh(bool newCode)
    {
        var server = _remote.Server;
        if (!ReferenceEquals(server, _subscribedServer))
        {
            if (_subscribedServer is not null)
                _subscribedServer.DevicePaired -= OnDevicePaired;
            if (server is not null)
                server.DevicePaired += OnDevicePaired;
            _subscribedServer = server;
        }

        EnabledBox.IsChecked = server is not null;
        StatusText.Text = _remote.Status;
        PairingPanel.Visibility = server is null ? Visibility.Collapsed : Visibility.Visible;

        if (server is not null)
        {
            var code = newCode || server.Pairing.Current is null ? server.Pairing.Start() : server.Pairing.Current!;
            var addresses = NetworkInfo.LanAddresses();
            CodeText.Text = $"{code[..3]} {code[3..]}";
            AddressText.Text = addresses.Count == 0
                ? "Nessuna rete trovata"
                : string.Join("\n", addresses.Take(3).Select(a => $"{a}:{server.Port}"));
            QrImage.Source = RenderQr(NetworkInfo.PairingLink(addresses, server.Port, code, Environment.MachineName));
        }

        var devices = _remote.Devices.Devices;
        DevicesList.ItemsSource = devices.Select(d => new { d.Id, d.Name, PairedOn = d.PairedOn.ToLocalTime() }).ToList();
        NoDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Guid id)
            return;
        if (MessageBox.Show(this, "Il telefono non potrà più controllare il PC finché non lo abbini di nuovo. Rimuoverlo?",
                "SoundBoardKing", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _remote.RemoveDevice(id);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _remote.StatusChanged -= OnStatusChanged;
        _remote.Devices.Changed -= OnDevicesChanged;
        if (_subscribedServer is not null)
        {
            _subscribedServer.DevicePaired -= OnDevicePaired;
            _subscribedServer.Pairing.Stop();
        }
    }

    private static BitmapImage RenderQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
