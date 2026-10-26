using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using KiteShell.Models;

namespace KiteShell.Views;

public partial class SessionEditorWindow : Window
{
    private SessionNode _draft = new();

    public SessionEditorWindow()
    {
        InitializeComponent();
        AccentCombo.ItemsSource = SessionNode.AccentColors.Select(c => c.Name).ToList();
        AuthCombo.SelectionChanged += (_, _) => UpdateAuthPanels();
        Opened += (_, _) => NameBox.Focus();
    }

    public SessionNode? Result { get; private set; }

    public string GroupPath { get; private set; } = string.Empty;

    public string? EnteredPassword { get; private set; }

    public string? EnteredPassphrase { get; private set; }

    public void Load(
        SessionNode node,
        string? groupPath,
        bool isNew,
        bool hasStoredPassword,
        bool hasStoredPassphrase)
    {
        _draft = node.Clone(newIds: false);
        DialogTitle.Text = isNew ? "新建 SSH 会话" : "编辑 SSH 会话";
        NameBox.Text = isNew ? string.Empty : node.Name;
        GroupBox.Text = groupPath ?? string.Empty;
        HostBox.Text = node.Host;
        PortBox.Text = node.Port.ToString();
        UserBox.Text = node.Username;
        AuthCombo.SelectedIndex = node.Auth == SshAuthMethod.PrivateKey ? 1 : 0;
        SavePasswordCheck.IsChecked = node.SavePassword;
        SavePassphraseCheck.IsChecked = node.SavePassword;
        PasswordBox.Text = string.Empty;
        PassphraseBox.Text = string.Empty;
        PasswordBox.PlaceholderText = hasStoredPassword ? "已保存密码，留空保持不变" : "连接时询问";
        PassphraseBox.PlaceholderText = hasStoredPassphrase ? "已保存口令，留空保持不变" : "连接时询问";
        KeyBox.Text = node.PrivateKeyPath ?? string.Empty;
        StartupBox.Text = node.StartupCommand ?? string.Empty;
        var accentIndex = SessionNode.AccentColors
            .Select((item, index) => (item, index))
            .FirstOrDefault(pair => string.Equals(pair.item.Hex, node.Accent, StringComparison.OrdinalIgnoreCase))
            .index;
        AccentCombo.SelectedIndex = accentIndex;
        UpdateAuthPanels();
    }

    private void UpdateAuthPanels()
    {
        var useKey = AuthCombo.SelectedIndex == 1;
        PasswordPanel.IsVisible = !useKey;
        KeyPanel.IsVisible = useKey;
    }

    private async void OnBrowseKeyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 SSH 私钥",
            AllowMultiple = false,
        });

        var file = files.FirstOrDefault();
        if (file is null)
        {
            return;
        }

        var localPath = file.TryGetLocalPath();
        KeyBox.Text = localPath ?? file.Path.LocalPath;
    }

    private void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        var host = HostBox.Text?.Trim() ?? string.Empty;
        var user = UserBox.Text?.Trim() ?? string.Empty;
        var portText = PortBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(host))
        {
            ShowError("请填写主机地址。");
            return;
        }

        if (string.IsNullOrWhiteSpace(user))
        {
            ShowError("请填写用户名。");
            return;
        }

        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            ShowError("端口必须是 1 到 65535 之间的数字。");
            return;
        }

        var useKey = AuthCombo.SelectedIndex == 1;
        var keyPath = KeyBox.Text?.Trim();
        if (useKey && string.IsNullOrWhiteSpace(keyPath))
        {
            ShowError("私钥认证需要选择私钥文件。");
            return;
        }

        _draft.Name = string.IsNullOrWhiteSpace(NameBox.Text)
            ? $"{user}@{host}"
            : NameBox.Text.Trim();
        _draft.Type = SessionNodeType.Session;
        _draft.Host = host;
        _draft.Port = port;
        _draft.Username = user;
        _draft.Auth = useKey ? SshAuthMethod.PrivateKey : SshAuthMethod.Password;
        _draft.PrivateKeyPath = string.IsNullOrWhiteSpace(keyPath) ? null : keyPath;
        _draft.StartupCommand = string.IsNullOrWhiteSpace(StartupBox.Text) ? null : StartupBox.Text.Trim();
        _draft.SavePassword = SavePasswordCheck.IsChecked == true;
        _draft.Accent = SessionNode.AccentColors[
            Math.Clamp(AccentCombo.SelectedIndex, 0, SessionNode.AccentColors.Count - 1)].Hex;

        var password = PasswordBox.Text;
        EnteredPassword = string.IsNullOrEmpty(password) ? null : password;
        var passphrase = PassphraseBox.Text;
        EnteredPassphrase = string.IsNullOrEmpty(passphrase) ? null : passphrase;

        if (!_draft.SavePassword)
        {
            _draft.PasswordCipher = null;
            _draft.PassphraseCipher = null;
        }

        GroupPath = NormalizeGroupPath(GroupBox.Text);
        Result = _draft;
        Close(true);
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private static string NormalizeGroupPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('/', segments);
    }
}
