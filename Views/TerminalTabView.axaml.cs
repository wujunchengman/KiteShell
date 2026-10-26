using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KiteShell.Models;
using KiteShell.Services;
using Renci.SshNet.Common;

namespace KiteShell.Views;

public enum TerminalConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}

public partial class TerminalTabView : UserControl
{
    private readonly SolidColorBrush _mutedBrush = NewBrush("#8B94A7");
    private readonly SolidColorBrush _warningBrush = NewBrush("#D29922");
    private readonly SolidColorBrush _successBrush = NewBrush("#3FB950");
    private readonly SolidColorBrush _dangerBrush = NewBrush("#F85149");

    private SessionNode _profile = new();
    private SessionSecrets _secrets = new();
    private MainWindow _owner = null!;
    private KnownHostsStore _knownHosts = null!;
    private SshSession? _session;
    private SftpService? _sftp;
    private CancellationTokenSource? _connectCts;
    private bool _sftpVisible;
    private bool _sftpBusy;
    private bool _closing;
    private bool _connecting;
    private string _sftpPath = "/";
    private string _terminalSize = "80 × 24";

    public TerminalTabView()
    {
        InitializeComponent();
        Terminal.UserInput += OnTerminalUserInput;
        Terminal.Resized += OnTerminalResized;
        Terminal.TitleChanged += title => TitleChanged?.Invoke(this, title);

        DragDrop.SetAllowDrop(SftpPanel, true);
        DragDrop.SetAllowDrop(SftpList, true);
        DragDrop.AddDragEnterHandler(SftpPanel, OnSftpDragEnter);
        DragDrop.AddDragOverHandler(SftpPanel, OnSftpDragOver);
        DragDrop.AddDragLeaveHandler(SftpPanel, OnSftpDragLeave);
        DragDrop.AddDropHandler(SftpPanel, OnSftpDrop);
    }

    public SessionNode Profile => _profile;

    public TerminalConnectionState State { get; private set; } = TerminalConnectionState.Disconnected;

    public string StateMessage { get; private set; } = "未连接";

    public string Target => _profile.Target;

    public string TerminalSizeText => _terminalSize;

    public IBrush StateBrush => State switch
    {
        TerminalConnectionState.Connected => _successBrush,
        TerminalConnectionState.Connecting => _warningBrush,
        TerminalConnectionState.Failed => _dangerBrush,
        _ => _mutedBrush,
    };

    public event EventHandler? StatusChanged;

    public event Action<TerminalTabView, string?>? TitleChanged;

    public void Configure(
        SessionNode profile,
        SessionSecrets secrets,
        MainWindow owner,
        KnownHostsStore knownHosts)
    {
        _profile = profile;
        _secrets = secrets;
        _owner = owner;
        _knownHosts = knownHosts;
        _sftpPath = "/";
        UpdateState(TerminalConnectionState.Disconnected, "未连接");
        SetSftpVisible(false);
    }

    private IStorageProvider StorageProvider =>
        TopLevel.GetTopLevel(this)?.StorageProvider
        ?? throw new InvalidOperationException("窗口尚未初始化。");

    public async Task ConnectAsync()
    {
        if (_connecting)
        {
            return;
        }

        _connecting = true;
        _closing = false;
        DisconnectSession(updateState: false);
        Terminal.ResetTerminal();
        DisconnectedBanner.IsVisible = false;
        SetConnectingOverlay(true);
        UpdateState(TerminalConnectionState.Connecting, "连接中...");

        var cts = new CancellationTokenSource();
        _connectCts = cts;
        var session = new SshSession();
        session.TextReceived += HandleSessionText;
        session.Closed += HandleSessionClosed;
        _session = session;

        try
        {
            var interaction = CreateInteraction();
            var settings = ConnectionSettings.FromProfile(_profile);
            var columns = (uint)Math.Max(2, Terminal.Buffer.Columns);
            var rows = (uint)Math.Max(2, Terminal.Buffer.Rows);
            await session.ConnectAsync(settings, interaction, columns, rows);

            if (cts.IsCancellationRequested || !ReferenceEquals(_session, session))
            {
                session.Dispose();
                return;
            }

            _secrets = interaction.Secrets;
            UpdateState(TerminalConnectionState.Connected, "已连接");
            Terminal.Focus();
            _owner.NotifySessionConnected(_profile);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }

            session.TextReceived -= HandleSessionText;
            session.Closed -= HandleSessionClosed;
            session.Dispose();

            if (cts.IsCancellationRequested)
            {
                return;
            }

            var message = DescribeError(ex);
            Terminal.AppendLocalLine($"\u001B[31m连接失败：{message}\u001B[0m");
            UpdateState(TerminalConnectionState.Failed, "连接失败");
        }
        finally
        {
            if (ReferenceEquals(_connectCts, cts))
            {
                _connectCts = null;
                _connecting = false;
                SetConnectingOverlay(false);
                ReconnectButton.IsEnabled = State != TerminalConnectionState.Connecting;
            }

            cts.Dispose();
        }
    }

    public void CloseGracefully()
    {
        _closing = true;
        try
        {
            _connectCts?.Cancel();
        }
        catch
        {
            // 忽略。
        }

        DisconnectSession(updateState: false);
        _sftp?.Dispose();
        _sftp = null;
    }

    public void SendSnippet(Snippet snippet)
    {
        Terminal.SendTextToSession(snippet.Command, snippet.AppendEnter);
    }

    public void FocusTerminal() => Terminal.Focus();

    private void HandleSessionText(object? sender, string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closing && ReferenceEquals(sender, _session))
            {
                Terminal.Feed(text);
            }
        });
    }

    private void HandleSessionClosed(object? sender, string reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing || !ReferenceEquals(sender, _session))
            {
                return;
            }

            DisconnectSession(updateState: false);
            Terminal.AppendLocalLine($"\r\n\u001B[38;5;244m{reason}\u001B[0m");
            UpdateState(TerminalConnectionState.Disconnected, "连接已断开");
            DisconnectedText.Text = reason;
            DisconnectedBanner.IsVisible = true;
        });
    }

    private void DisconnectSession(bool updateState)
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.TextReceived -= HandleSessionText;
            session.Closed -= HandleSessionClosed;
            session.Dispose();
        }

        if (updateState)
        {
            UpdateState(TerminalConnectionState.Disconnected, "已断开");
        }
    }

    private SshInteraction CreateInteraction()
    {
        return new SshInteraction
        {
            Profile = _profile,
            Secrets = _secrets,
            KnownHosts = _knownHosts,
            VerifyHostKey = info => RunOnUi(() => _owner.ShowHostKeyDialogAsync(info)),
            Prompt = info => RunOnUi(() => _owner.ShowAuthPromptAsync(info)),
        };
    }

    private static T RunOnUi<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.SetResult(await action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        return completion.Task.GetAwaiter().GetResult();
    }

    private void OnTerminalUserInput(string text)
    {
        _session?.Send(text);
    }

    private void OnTerminalResized(int columns, int rows)
    {
        _terminalSize = $"{columns} × {rows}";
        SizeText.Text = _terminalSize;
        _session?.Resize((uint)columns, (uint)rows);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateState(TerminalConnectionState state, string message)
    {
        State = state;
        StateMessage = message;
        StateDot.Fill = StateBrush;
        StateText.Text = message;
        StateText.Foreground = StateBrush;
        ReconnectButton.IsEnabled = state != TerminalConnectionState.Connecting;
        SftpButton.IsEnabled = state == TerminalConnectionState.Connected || _sftpVisible;
        StatusChanged?.Invoke(this, EventArgs.Empty);

        if (state == TerminalConnectionState.Connected && _sftpVisible)
        {
            _ = EnsureSftpAsync();
        }
        else if (state != TerminalConnectionState.Connected)
        {
            HideSftpDropOverlay();
        }
    }

    private void SetConnectingOverlay(bool visible)
    {
        ConnectingOverlay.IsVisible = visible;
        ConnectingText.Text = State == TerminalConnectionState.Connecting
            ? "正在建立 SSH 连接..."
            : "正在连接...";
    }

    private void OnCancelConnectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var cts = _connectCts;
        _connectCts = null;
        _connecting = false;
        try
        {
            cts?.Cancel();
        }
        catch
        {
            // 忽略。
        }

        SetConnectingOverlay(false);
        DisconnectSession(updateState: false);
        Terminal.AppendLocalLine("\u001B[38;5;244m已取消连接。\u001B[0m");
        UpdateState(TerminalConnectionState.Disconnected, "已取消连接");
        ReconnectButton.IsEnabled = true;
    }

    private async void OnReconnectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await ConnectAsync();
    }

    private void OnClearClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Terminal.ResetTerminal();
        Terminal.Focus();
    }

    private void OnToggleSftpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetSftpVisible(!_sftpVisible);
    }

    private void OnSftpCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetSftpVisible(false);
    }

    private void SetSftpVisible(bool visible)
    {
        _sftpVisible = visible;
        SftpPanel.IsVisible = visible;
        SftpSplitter.IsVisible = visible;
        RootGrid.ColumnDefinitions[1].Width = visible ? new GridLength(4) : new GridLength(0);
        RootGrid.ColumnDefinitions[2].Width = visible ? new GridLength(420) : new GridLength(0);
        SftpButton.Content = visible ? "关闭 SFTP" : "SFTP";
        SftpButton.IsEnabled = State == TerminalConnectionState.Connected || visible;

        if (!visible)
        {
            HideSftpDropOverlay();
        }

        if (visible)
        {
            if (string.IsNullOrEmpty(SftpPathBox.Text))
            {
                SftpPathBox.Text = _sftpPath;
                SftpPathHint.Text = _sftpPath;
            }

            _ = EnsureSftpAsync();
        }
    }

    private async Task EnsureSftpAsync()
    {
        if (_session is not { IsConnected: true })
        {
            SftpStatusText.Text = "请先连接 SSH 会话";
            return;
        }

        if (_sftp is not null && _sftp.IsConnected)
        {
            await LoadSftpPathAsync(_sftpPath);
            return;
        }

        SetSftpBusy(true);
        SftpStatusText.Text = "正在连接 SFTP...";
        try
        {
            _sftp ??= new SftpService();
            var interaction = CreateInteraction();
            await _sftp.ConnectAsync(ConnectionSettings.FromProfile(_profile), interaction);
            _secrets = interaction.Secrets;
            _sftpPath = _sftp.HomePath;
            await LoadSftpPathAsync(_sftpPath);
        }
        catch (Exception ex)
        {
            SftpStatusText.Text = "SFTP 连接失败：" + DescribeError(ex);
        }
        finally
        {
            SetSftpBusy(false);
        }
    }

    private async Task LoadSftpPathAsync(string input, bool manageBusy = true)
    {
        if (_sftp is not { IsConnected: true })
        {
            return;
        }

        if (manageBusy)
        {
            SetSftpBusy(true);
        }

        try
        {
            var normalized = _sftp.NormalizePath(_sftpPath, input);
            var entries = await _sftp.ListAsync(normalized);
            _sftpPath = normalized;
            SftpPathBox.Text = normalized;
            SftpPathHint.Text = normalized;
            SftpList.ItemsSource = entries;
            SftpStatusText.Text = $"可拖拽上传 · {entries.Count} 项";
        }
        catch (Exception ex)
        {
            SftpStatusText.Text = "读取目录失败：" + DescribeError(ex);
        }
        finally
        {
            if (manageBusy)
            {
                SetSftpBusy(false);
            }
        }
    }

    private void SetSftpBusy(bool busy)
    {
        _sftpBusy = busy;
        SftpProgress.IsVisible = busy;
        SftpProgress.IsIndeterminate = busy;
    }

    private void OnSftpDragEnter(object? sender, DragEventArgs e) => UpdateSftpDragState(e);

    private void OnSftpDragOver(object? sender, DragEventArgs e) => UpdateSftpDragState(e);

    private void OnSftpDragLeave(object? sender, DragEventArgs e) => HideSftpDropOverlay();

    private async void OnSftpDrop(object? sender, DragEventArgs e)
    {
        HideSftpDropOverlay();
        e.Handled = true;

        if (!_sftpVisible || _sftpBusy || _sftp is not { IsConnected: true })
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var items = await ResolveDraggedItemsAsync(e.DataTransfer);
        if (items.Count == 0)
        {
            SftpStatusText.Text = "未识别到可上传的本地文件";
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        await UploadStorageItemsAsync(items);
    }

    private async Task<List<IStorageItem>> ResolveDraggedItemsAsync(IDataTransfer data)
    {
        var result = new List<IStorageItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var files = data.TryGetFiles();
            if (files is not null)
            {
                foreach (var file in files)
                {
                    if (seen.Add(file.Path.AbsoluteUri))
                    {
                        result.Add(file);
                    }
                }
            }
        }
        catch
        {
            // 某些平台不支持文件格式，继续尝试 URI 文本。
        }

        if (result.Count > 0)
        {
            return result;
        }

        string? text = null;
        try
        {
            text = data.TryGetText();
        }
        catch
        {
            // 忽略。
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0
                || line[0] == '#'
                || !Uri.TryCreate(line, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeFile)
            {
                continue;
            }

            if (!seen.Add(uri.AbsoluteUri))
            {
                continue;
            }

            try
            {
                var localPath = uri.LocalPath;
                IStorageItem? item = Directory.Exists(localPath)
                    ? await StorageProvider.TryGetFolderFromPathAsync(uri)
                    : await StorageProvider.TryGetFileFromPathAsync(uri);
                if (item is not null)
                {
                    result.Add(item);
                }
            }
            catch
            {
                // 跳过无法访问的路径。
            }
        }

        return result;
    }

    private void UpdateSftpDragState(DragEventArgs e)
    {
        var accepted = _sftpVisible
                       && !_sftpBusy
                       && _sftp is { IsConnected: true }
                       && HasLocalFiles(e.DataTransfer);

        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        if (accepted)
        {
            e.Handled = true;
            ShowSftpDropOverlay();
        }
        else
        {
            HideSftpDropOverlay();
        }
    }

    private static bool HasLocalFiles(IDataTransfer data)
    {
        try
        {
            var files = data.TryGetFiles();
            if (files is { Length: > 0 })
            {
                return true;
            }

            var text = data.TryGetText();
            return text is not null
                   && text.Contains("file://", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void ShowSftpDropOverlay()
    {
        SftpDropText.Text = "目标目录：" + _sftpPath;
        SftpDropOverlay.IsVisible = true;
    }

    private void HideSftpDropOverlay()
    {
        SftpDropOverlay.IsVisible = false;
    }

    private List<RemoteEntry> GetSelectedEntries()
    {
        return SftpList.SelectedItems?.OfType<RemoteEntry>().ToList() ?? new List<RemoteEntry>();
    }

    private async void OnSftpRefreshClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await LoadSftpPathAsync(_sftpPath);
    }

    private async void OnSftpUpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await LoadSftpPathAsync("..");
    }

    private async void OnSftpGoClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await LoadSftpPathAsync(SftpPathBox.Text ?? _sftpPath);
    }

    private async void OnSftpPathKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await LoadSftpPathAsync(SftpPathBox.Text ?? _sftpPath);
    }

    private async void OnSftpListDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        var entry = GetSelectedEntries().FirstOrDefault();
        if (entry is null)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            await LoadSftpPathAsync(entry.FullPath);
        }
        else
        {
            await DownloadSingleAsync(entry);
        }
    }

    private async void OnSftpUploadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sftp is not { IsConnected: true } || _sftpBusy)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "上传文件到 " + _sftpPath,
            AllowMultiple = true,
        });

        if (files.Count == 0)
        {
            return;
        }

        await UploadStorageItemsAsync(files);
    }

    private async Task UploadStorageItemsAsync(IReadOnlyList<IStorageItem> items)
    {
        if (_sftp is not { IsConnected: true })
        {
            return;
        }

        var targetDirectory = _sftpPath;
        SetSftpBusy(true);
        var uploaded = 0;
        var failed = 0;
        try
        {
            SftpStatusText.Text = $"正在上传 {items.Count} 项到 {targetDirectory} ...";
            foreach (var item in items)
            {
                try
                {
                    uploaded += await UploadStorageItemAsync(item, targetDirectory, 0);
                }
                catch (Exception ex)
                {
                    failed++;
                    SftpStatusText.Text = $"上传 {item.Name} 失败：{DescribeError(ex)}";
                }
            }

            await LoadSftpPathAsync(_sftpPath, manageBusy: false);
            SftpStatusText.Text = failed == 0
                ? $"已上传 {uploaded} 个文件"
                : $"已上传 {uploaded} 个文件，{failed} 项失败";
        }
        finally
        {
            SetSftpBusy(false);
        }
    }

    private async Task<int> UploadStorageItemAsync(IStorageItem item, string remoteDirectory, int depth)
    {
        if (_sftp is null || depth > 32)
        {
            return 0;
        }

        if (item is IStorageFolder folder)
        {
            var folderName = SanitizeRemoteName(folder.Name, "新目录");
            var remoteFolder = SftpService.JoinRemotePath(remoteDirectory, folderName);
            if (!await _sftp.ExistsAsync(remoteFolder))
            {
                await _sftp.CreateDirectoryAsync(remoteFolder);
            }

            SftpStatusText.Text = "正在上传目录 " + folderName + " ...";
            var count = 0;
            await foreach (var child in folder.GetItemsAsync())
            {
                count += await UploadStorageItemAsync(child, remoteFolder, depth + 1);
            }

            return count;
        }

        if (item is IStorageFile file)
        {
            var fileName = SanitizeRemoteName(file.Name, "upload.bin");
            SftpStatusText.Text = "正在上传 " + fileName + " ...";
            await using var stream = await file.OpenReadAsync();
            await _sftp.UploadAsync(stream, SftpService.JoinRemotePath(remoteDirectory, fileName));
            return 1;
        }

        return 0;
    }

    private static string SanitizeRemoteName(string? name, string fallback)
    {
        var value = (name ?? string.Empty).Trim().Replace('/', '_').Replace('\\', '_');
        return value.Length == 0 || value is "." or ".." ? fallback : value;
    }

    private async void OnSftpDownloadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sftp is not { IsConnected: true })
        {
            return;
        }

        var selected = GetSelectedEntries();
        if (selected.Count == 0)
        {
            SftpStatusText.Text = "请选择要下载的文件";
            return;
        }

        if (selected.Count == 1 && !selected[0].IsDirectory)
        {
            await DownloadSingleAsync(selected[0]);
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择下载目录",
            AllowMultiple = false,
        });

        var localFolder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(localFolder))
        {
            SftpStatusText.Text = "无法访问所选本地目录";
            return;
        }

        SetSftpBusy(true);
        var count = 0;
        try
        {
            foreach (var entry in selected)
            {
                count += await DownloadRecursiveAsync(entry, localFolder);
            }

            SftpStatusText.Text = $"已下载 {count} 个文件到 {localFolder}";
        }
        catch (Exception ex)
        {
            SftpStatusText.Text = "下载失败：" + DescribeError(ex);
        }
        finally
        {
            SetSftpBusy(false);
        }
    }

    private async Task DownloadSingleAsync(RemoteEntry entry)
    {
        if (_sftp is not { IsConnected: true })
        {
            return;
        }

        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存 " + entry.Name,
            SuggestedFileName = entry.Name,
        });

        if (target is null)
        {
            return;
        }

        SetSftpBusy(true);
        try
        {
            await using var stream = await target.OpenWriteAsync();
            await _sftp.DownloadAsync(entry.FullPath, stream);
            SftpStatusText.Text = $"已下载 {entry.Name}";
        }
        catch (Exception ex)
        {
            SftpStatusText.Text = "下载失败：" + DescribeError(ex);
        }
        finally
        {
            SetSftpBusy(false);
        }
    }

    private async Task<int> DownloadRecursiveAsync(RemoteEntry entry, string localDirectory)
    {
        if (_sftp is null)
        {
            return 0;
        }

        var localPath = Path.Combine(localDirectory, entry.Name);
        if (entry.IsDirectory && !entry.IsSymbolicLink)
        {
            Directory.CreateDirectory(localPath);
            var count = 0;
            foreach (var child in await _sftp.ListAsync(entry.FullPath))
            {
                count += await DownloadRecursiveAsync(child, localPath);
            }

            return count;
        }

        await using var stream = File.Create(localPath);
        await _sftp.DownloadAsync(entry.FullPath, stream);
        return 1;
    }

    private async void OnSftpNewFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sftp is not { IsConnected: true })
        {
            return;
        }

        var name = await _owner.PromptTextAsync("新建目录", "请输入目录名称", "新建目录");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        SetSftpBusy(true);
        try
        {
            await _sftp.CreateDirectoryAsync(SftpService.JoinRemotePath(_sftpPath, name.Trim()));
            await LoadSftpPathAsync(_sftpPath);
        }
        catch (Exception ex)
        {
            SftpStatusText.Text = "创建目录失败：" + DescribeError(ex);
        }
        finally
        {
            SetSftpBusy(false);
        }
    }

    private async void OnSftpRenameClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sftp is not { IsConnected: true })
        {
            return;
        }

        var entry = GetSelectedEntries().FirstOrDefault();
        if (entry is null)
        {
            SftpStatusText.Text = "请选择要重命名的项目";
            return;
        }

        var name = await _owner.PromptTextAsync("重命名", $"为 “{entry.Name}” 输入新名称", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == entry.Name)
        {
            return;
        }

        SetSftpBusy(true);
        try
        {
            var parent = GetRemoteParent(entry.FullPath);
            await _sftp.RenameAsync(entry.FullPath, SftpService.JoinRemotePath(parent, name.Trim()));
            await LoadSftpPathAsync(_sftpPath);
        }
        catch (Exception ex)
        {
            SftpStatusText.Text = "重命名失败：" + DescribeError(ex);
        }
        finally
        {
            SetSftpBusy(false);
        }
    }

    private async void OnSftpDeleteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sftp is not { IsConnected: true })
        {
            return;
        }

        var selected = GetSelectedEntries();
        if (selected.Count == 0)
        {
            SftpStatusText.Text = "请选择要删除的项目";
            return;
        }

        var message = selected.Count == 1
            ? $"确定要删除 “{selected[0].Name}” 吗？"
            : $"确定要删除选中的 {selected.Count} 个项目吗？";
        if (selected.Any(item => item.IsDirectory && !item.IsSymbolicLink))
        {
            message += "\n目录及其内容会被递归删除，此操作不可撤销。";
        }

        if (!await _owner.ConfirmAsync("删除远端文件", message, "删除", danger: true))
        {
            return;
        }

        SetSftpBusy(true);
        try
        {
            foreach (var entry in selected)
            {
                await _sftp.DeleteAsync(entry);
            }

            await LoadSftpPathAsync(_sftpPath);
            SftpStatusText.Text = $"已删除 {selected.Count} 项";
        }
        catch (Exception ex)
        {
            SftpStatusText.Text = "删除失败：" + DescribeError(ex);
        }
        finally
        {
            SetSftpBusy(false);
        }
    }

    private static string GetRemoteParent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private static string DescribeError(Exception ex)
    {
        if (ex is AggregateException aggregate && aggregate.InnerException is not null)
        {
            ex = aggregate.InnerException;
        }

        if (ex is SshAuthenticationException)
        {
            return "身份验证失败：请检查用户名、密码或私钥。";
        }

        if (ex is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "连接被拒绝：请确认主机和端口正确，且 SSH 服务已启动。",
                SocketError.HostNotFound or SocketError.NoData => "无法解析主机名。",
                SocketError.TimedOut => "连接超时。",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => "网络不可达。",
                _ => socket.Message,
            };
        }

        var message = ex.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return ex.GetType().Name;
        }

        if (message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
        {
            return "连接被拒绝：请确认主机和端口正确，且 SSH 服务已启动。";
        }

        if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase))
        {
            return "无法解析主机名。";
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "连接超时。";
        }

        if (message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return "身份验证被拒绝。";
        }

        return message;
    }

    private static SolidColorBrush NewBrush(string color) =>
        new(Color.Parse(color));
}
