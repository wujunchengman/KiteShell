using Avalonia.Controls;
using KiteShell.Services;

namespace KiteShell.Views;

public partial class HostKeyWindow : Window
{
    public HostKeyWindow()
    {
        InitializeComponent();
    }

    public HostKeyWindow(HostKeyPromptInfo info)
        : this()
    {
        HostText.Text = $"{info.Host}:{info.Port}";
        KeyTypeText.Text = info.KeyType;
        FingerprintBox.Text = info.Fingerprint;

        if (info.Changed)
        {
            HeaderText.Text = "主机密钥已更改";
            WarningText.Text = "该主机的密钥与之前记录的不一致。这可能意味着服务器重装过，也可能存在中间人攻击。请务必核实后再继续。";
            WarningBox.IsVisible = true;
        }
    }

    private void OnRejectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close(HostKeyAction.Reject);

    private void OnTrustOnceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close(HostKeyAction.TrustOnce);

    private void OnTrustSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close(HostKeyAction.TrustAndSave);
}
