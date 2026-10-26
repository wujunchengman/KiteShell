using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using KiteShell.Models;
using KiteShell.Services;
using KiteShell.ViewModels;
using KiteShell.Views;

namespace KiteShell;

public partial class MainWindow : Window
{
    private readonly SessionStore _store = new(AppPaths.SessionsFile);
    private readonly SnippetStore _snippetStore = new(AppPaths.SnippetsFile);
    private readonly KnownHostsStore _knownHosts = new(AppPaths.KnownHostsFile);
    private readonly SecretProtector _protector = new(AppPaths.SecretKeyFile);
    private readonly ObservableCollection<SessionNodeViewModel> _tree = new();
    private readonly List<OpenTab> _openTabs = new();
    private readonly SolidColorBrush _mutedBrush = new(Color.Parse("#8B94A7"));
    private SessionNodeViewModel? _selectedNode;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();

        _store.Load();
        _snippetStore.Load();

        SessionTree.ItemsSource = _tree;
        SnippetList.ItemsSource = _snippetStore.Items;

        LoadTree();
        UpdateWelcome();
        UpdateStatusBar();

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Closing += (_, _) =>
        {
            foreach (var tab in _openTabs.ToList())
            {
                tab.View.CloseGracefully();
            }
        };

        _initialized = true;
    }

    public async Task<HostKeyAction> ShowHostKeyDialogAsync(HostKeyPromptInfo info)
    {
        var dialog = new HostKeyWindow(info);
        return await dialog.ShowDialog<HostKeyAction>(this);
    }

    public Task<string?> ShowAuthPromptAsync(AuthenticationPromptInfo info)
    {
        var message = string.IsNullOrWhiteSpace(info.Instruction)
            ? info.Prompt
            : info.Instruction + "\n" + info.Prompt;
        return PromptWindow.AskAsync(
            this,
            "身份验证",
            message,
            password: !info.Echo,
            okText: "提交",
            watermark: info.Echo ? "请输入" : "请输入密码");
    }

    public Task<string?> PromptTextAsync(string title, string message, string? initial = null) =>
        PromptWindow.AskAsync(this, title, message, initial);

    public Task<bool> ConfirmAsync(string title, string message, string okText = "确定", bool danger = false) =>
        PromptWindow.ConfirmAsync(this, title, message, okText, danger);

    public Task ShowMessageAsync(string title, string message) =>
        PromptWindow.ShowMessageAsync(this, title, message);

    public void NotifySessionConnected(SessionNode profile)
    {
        var stored = _store.FindById(profile.Id);
        if (stored is null)
        {
            return;
        }

        stored.LastConnectedUtc = DateTime.UtcNow;
        try
        {
            _store.Save();
        }
        catch
        {
            // 时间戳保存失败不影响使用。
        }
    }

    private void LoadTree()
    {
        var previousId = _selectedNode?.Node.Id;
        var filter = SessionSearchBox.Text?.Trim();
        var filtering = !string.IsNullOrEmpty(filter);
        var items = BuildViewModels(_store.Roots, filter, forceInclude: false, depth: 0);

        _tree.Clear();
        foreach (var item in items)
        {
            _tree.Add(item);
        }

        SessionCountText.Text = CountSessions(_store.Roots).ToString();
        _selectedNode = previousId is null ? null : FindViewModel(_tree, previousId);
        if (_selectedNode is not null)
        {
            SessionTree.SelectedItem = _selectedNode;
        }

        if (filtering && _tree.Count == 0)
        {
            SessionHintText.Text = "没有匹配的会话";
        }
    }

    private List<SessionNodeViewModel> BuildViewModels(
        IEnumerable<SessionNode> nodes,
        string? filter,
        bool forceInclude,
        int depth)
    {
        var result = new List<SessionNodeViewModel>();
        foreach (var node in nodes)
        {
            if (node.IsFolder)
            {
                var selfMatch = filter is null
                                || node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
                var children = BuildViewModels(node.Children, filter, forceInclude || selfMatch, depth + 1);
                if (filter is not null && !forceInclude && !selfMatch && children.Count == 0)
                {
                    continue;
                }

                var folderVm = new SessionNodeViewModel(node)
                {
                    IsExpanded = filter is not null || depth == 0,
                };
                foreach (var child in children)
                {
                    folderVm.Children.Add(child);
                }

                result.Add(folderVm);
                continue;
            }

            if (filter is not null && !forceInclude && !MatchesFilter(node, filter))
            {
                continue;
            }

            result.Add(new SessionNodeViewModel(node));
        }

        return result;
    }

    private static bool MatchesFilter(SessionNode node, string filter)
    {
        return node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || node.Host.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || node.Username.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || node.Target.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountSessions(IEnumerable<SessionNode> nodes)
    {
        var count = 0;
        foreach (var node in nodes)
        {
            count += node.IsFolder ? CountSessions(node.Children) : 1;
        }

        return count;
    }

    private static SessionNodeViewModel? FindViewModel(IEnumerable<SessionNodeViewModel> items, string id)
    {
        foreach (var item in items)
        {
            if (item.Node.Id == id)
            {
                return item;
            }

            var found = FindViewModel(item.Children, id);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private async void OnNewSessionClick(object? sender, RoutedEventArgs e)
    {
        await CreateSessionAsync(GetTargetGroupPath());
    }

    private async void OnNewFolderClick(object? sender, RoutedEventArgs e)
    {
        await CreateFolderAsync(GetTargetGroupPath());
    }

    private async void OnContextNewSessionClick(object? sender, RoutedEventArgs e)
    {
        await CreateSessionAsync(GetTargetGroupPath());
    }

    private async void OnContextNewFolderClick(object? sender, RoutedEventArgs e)
    {
        await CreateFolderAsync(GetTargetGroupPath());
    }

    private async void OnContextConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedNode is { IsFolder: false })
        {
            await OpenSessionAsync(_selectedNode.Node);
        }
    }

    private async void OnContextEditClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            return;
        }

        if (_selectedNode.IsFolder)
        {
            await RenameFolderAsync(_selectedNode.Node);
        }
        else
        {
            await EditSessionAsync(_selectedNode.Node);
        }
    }

    private void OnContextDuplicateClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedNode is not { IsFolder: false })
        {
            return;
        }

        var source = _selectedNode.Node;
        var parent = _store.GetParentGroupPath(source);
        var copy = source.Clone();
        copy.Name = source.Name + " 副本";
        _store.PlaceNode(copy, parent);
        _store.Save();
        LoadTree();
    }

    private async void OnImportSshConfigClick(object? sender, RoutedEventArgs e)
    {
        var configPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh",
            "config");
        if (!File.Exists(configPath))
        {
            await ShowMessageAsync("导入 SSH 配置", $"未找到配置文件：{configPath}");
            return;
        }

        List<ImportedSshSession> imported;
        try
        {
            imported = SshConfigImporter.Parse(configPath, Environment.UserName).ToList();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("导入 SSH 配置", "读取配置失败：" + ex.Message);
            return;
        }

        if (imported.Count == 0)
        {
            await ShowMessageAsync("导入 SSH 配置", "配置中没有可导入的 Host 条目。");
            return;
        }

        var existing = new HashSet<string>(
            EnumerateSessions(_store.Roots).Select(node => $"{node.Host}:{node.Port}:{node.Username}"),
            StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var item in imported)
        {
            var key = $"{item.Host}:{item.Port}:{item.Username}";
            if (!existing.Add(key))
            {
                continue;
            }

            _store.PlaceNode(new SessionNode
            {
                Name = item.Alias,
                Type = SessionNodeType.Session,
                Host = item.Host,
                Port = item.Port,
                Username = item.Username,
                Auth = string.IsNullOrWhiteSpace(item.IdentityFile)
                    ? SshAuthMethod.Password
                    : SshAuthMethod.PrivateKey,
                PrivateKeyPath = item.IdentityFile,
                Accent = SessionNode.AccentColors[added % SessionNode.AccentColors.Count].Hex,
            }, "SSH Config");
            added++;
        }

        _store.Save();
        LoadTree();
        await ShowMessageAsync(
            "导入 SSH 配置",
            $"已导入 {added} 个会话到 “SSH Config” 分组。\n跳过 {imported.Count - added} 个重复或无效条目。");
    }

    private static IEnumerable<SessionNode> EnumerateSessions(IEnumerable<SessionNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder)
            {
                foreach (var child in EnumerateSessions(node.Children))
                {
                    yield return child;
                }
            }
            else
            {
                yield return node;
            }
        }
    }

    private async void OnContextDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            return;
        }

        var node = _selectedNode.Node;
        var message = node.IsFolder
            ? $"确定要删除分组 “{node.Name}” 及其中的所有会话吗？"
            : $"确定要删除会话 “{node.Name}” 吗？";
        if (!await ConfirmAsync("删除", message, "删除", danger: true))
        {
            return;
        }

        _store.Remove(node);
        _store.Save();
        LoadTree();
        SessionHintText.Text = "已删除";
    }

    private async Task CreateSessionAsync(string groupPath)
    {
        var editor = new SessionEditorWindow();
        editor.Load(
            new SessionNode { Type = SessionNodeType.Session, Port = 22 },
            groupPath,
            isNew: true,
            hasStoredPassword: false,
            hasStoredPassphrase: false);

        if (await editor.ShowDialog<bool>(this) && editor.Result is { } node)
        {
            ApplyEditorSecrets(node, editor);
            _store.PlaceNode(node, editor.GroupPath);
            _store.Save();
            LoadTree();
            SessionHintText.Text = "已保存会话：" + node.Name;
        }
    }

    private async Task CreateFolderAsync(string parentPath)
    {
        var name = await PromptTextAsync("新建分组", "请输入分组名称", "新分组");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var folder = new SessionNode
        {
            Name = name.Trim(),
            Type = SessionNodeType.Folder,
        };
        _store.PlaceNode(folder, parentPath);
        _store.Save();
        LoadTree();
    }

    private async Task RenameFolderAsync(SessionNode folder)
    {
        var name = await PromptTextAsync("重命名分组", "请输入新的分组名称", folder.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == folder.Name)
        {
            return;
        }

        folder.Name = name.Trim();
        _store.Save();
        LoadTree();
    }

    private async Task EditSessionAsync(SessionNode node)
    {
        var clone = node.Clone(newIds: false);
        var editor = new SessionEditorWindow();
        editor.Load(
            clone,
            _store.GetParentGroupPath(node),
            isNew: false,
            hasStoredPassword: !string.IsNullOrEmpty(node.PasswordCipher),
            hasStoredPassphrase: !string.IsNullOrEmpty(node.PassphraseCipher));

        if (await editor.ShowDialog<bool>(this) && editor.Result is { } updated)
        {
            ApplyEditorSecrets(updated, editor);
            _store.PlaceNode(updated, editor.GroupPath);
            _store.Save();
            LoadTree();
            SessionHintText.Text = "已更新会话：" + updated.Name;
        }
    }

    private void ApplyEditorSecrets(SessionNode node, SessionEditorWindow editor)
    {
        if (!node.SavePassword)
        {
            node.PasswordCipher = null;
            node.PassphraseCipher = null;
            return;
        }

        if (!string.IsNullOrEmpty(editor.EnteredPassword))
        {
            node.PasswordCipher = _protector.Protect(editor.EnteredPassword);
        }

        if (!string.IsNullOrEmpty(editor.EnteredPassphrase))
        {
            node.PassphraseCipher = _protector.Protect(editor.EnteredPassphrase);
        }
    }

    private async Task OpenSessionAsync(SessionNode node)
    {
        var existing = _openTabs.FirstOrDefault(tab => tab.View.Profile.Id == node.Id);
        if (existing is not null)
        {
            SessionTabs.SelectedItem = existing.Item;
            existing.View.FocusTerminal();
            return;
        }

        var secrets = BuildStoredSecrets(node);
        if (node.Auth == SshAuthMethod.Password && string.IsNullOrEmpty(secrets.Password))
        {
            var prompt = await PromptWindow.AskPasswordAsync(
                this,
                "输入密码",
                $"{node.Username}@{node.Host}:{node.Port}");
            if (prompt is null)
            {
                return;
            }

            secrets = secrets with { Password = prompt.Password };
            if (prompt.Remember && !string.IsNullOrEmpty(prompt.Password))
            {
                node.SavePassword = true;
                node.PasswordCipher = _protector.Protect(prompt.Password);
                _store.Save();
            }
        }

        await CreateTabAsync(node, secrets);
    }

    private SessionSecrets BuildStoredSecrets(SessionNode node)
    {
        if (!node.SavePassword)
        {
            return new SessionSecrets();
        }

        return new SessionSecrets(
            _protector.Unprotect(node.PasswordCipher),
            _protector.Unprotect(node.PassphraseCipher));
    }

    private async void OnQuickConnectClick(object? sender, RoutedEventArgs e)
    {
        await QuickConnectAsync();
    }

    private async void OnQuickConnectKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await QuickConnectAsync();
    }

    private async Task QuickConnectAsync()
    {
        var host = QuickHostBox.Text?.Trim() ?? string.Empty;
        var user = QuickUserBox.Text?.Trim() ?? string.Empty;
        var port = 22;
        if (!string.IsNullOrWhiteSpace(QuickPortBox.Text)
            && (!int.TryParse(QuickPortBox.Text, out port) || port is < 1 or > 65535))
        {
            await ShowMessageAsync("快速连接", "端口号无效。");
            return;
        }

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
        {
            await ShowMessageAsync("快速连接", "请填写主机地址和用户名。");
            return;
        }

        var passwordPrompt = await PromptWindow.AskPasswordAsync(
            this,
            "快速连接",
            $"{user}@{host}:{port}\n输入密码（留空则尝试其他认证方式）",
            showRemember: false);
        if (passwordPrompt is null)
        {
            return;
        }

        var node = new SessionNode
        {
            Name = $"{user}@{host}",
            Type = SessionNodeType.Session,
            Host = host,
            Port = port,
            Username = user,
            Auth = SshAuthMethod.Password,
            Accent = SessionNode.AccentColors[0].Hex,
        };

        await CreateTabAsync(node, new SessionSecrets(passwordPrompt.Password));
    }

    private async Task CreateTabAsync(SessionNode profile, SessionSecrets secrets)
    {
        var view = new TerminalTabView();
        view.Configure(profile, secrets, this, _knownHosts);

        var header = CreateTabHeader(view);
        var item = new TabItem
        {
            Header = header.Container,
            Content = view,
        };

        SessionTabs.Items.Add(item);
        SessionTabs.SelectedItem = item;

        var tab = new OpenTab(view, item, header.Dot, header.Title);
        _openTabs.Add(tab);
        view.StatusChanged += OnTabStatusChanged;
        view.TitleChanged += OnTabTitleChanged;

        UpdateWelcome();
        UpdateStatusBar();
        await view.ConnectAsync();
        view.FocusTerminal();
    }

    private TabHeader CreateTabHeader(TerminalTabView view)
    {
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = view.StateBrush,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var title = new TextBlock
        {
            Text = view.Profile.Name,
            FontSize = 12,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var target = new TextBlock
        {
            Text = view.Target,
            FontSize = 9.5,
            MaxWidth = 140,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _mutedBrush,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var closeIcon = new PathIcon
        {
            Data = StreamGeometry.Parse("M6.4 5 12 10.6 17.6 5 19 6.4 13.4 12 19 17.6 17.6 19 12 13.4 6.4 19 5 17.6 10.6 12 5 6.4z"),
            Width = 9,
            Height = 9,
        };
        var close = new Button
        {
            Content = closeIcon,
            Margin = new Thickness(4, 0, 0, 0),
        };
        close.Classes.Add("icon");
        close.Classes.Add("tiny");
        ToolTip.SetTip(close, "关闭标签（Ctrl+Shift+W）");
        close.Click += (_, _) => CloseTab(view);

        var panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        panel.Children.Add(dot);
        panel.Children.Add(title);
        panel.Children.Add(target);
        panel.Children.Add(close);

        return new TabHeader(panel, dot, title);
    }

    private void OnTabStatusChanged(object? sender, EventArgs e)
    {
        if (sender is not TerminalTabView view)
        {
            return;
        }

        var tab = _openTabs.FirstOrDefault(item => item.View == view);
        if (tab is null)
        {
            return;
        }

        tab.Dot.Fill = view.StateBrush;
        if (ReferenceEquals(SessionTabs.SelectedItem, tab.Item))
        {
            UpdateStatusBar();
        }
    }

    private void OnTabTitleChanged(TerminalTabView view, string? title)
    {
        var tab = _openTabs.FirstOrDefault(item => item.View == view);
        if (tab is null || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        ToolTip.SetTip(tab.Title, title);
    }

    private void CloseTab(TerminalTabView view)
    {
        var tab = _openTabs.FirstOrDefault(item => item.View == view);
        if (tab is null)
        {
            return;
        }

        var wasSelected = ReferenceEquals(SessionTabs.SelectedItem, tab.Item);
        var selectedIndex = SessionTabs.SelectedIndex;
        view.StatusChanged -= OnTabStatusChanged;
        view.TitleChanged -= OnTabTitleChanged;
        view.CloseGracefully();
        SessionTabs.Items.Remove(tab.Item);
        _openTabs.Remove(tab);

        if (wasSelected && _openTabs.Count > 0)
        {
            SessionTabs.SelectedIndex = Math.Clamp(selectedIndex, 0, _openTabs.Count - 1);
        }

        UpdateWelcome();
        UpdateStatusBar();
    }

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateStatusBar();

        if (SessionTabs.SelectedItem is TabItem { Content: TerminalTabView view })
        {
            view.FocusTerminal();
        }
    }

    private void UpdateWelcome()
    {
        var hasTabs = SessionTabs.Items.Count > 0;
        WelcomePanel.IsVisible = !hasTabs;
        SessionTabs.IsVisible = hasTabs;
    }

    private void UpdateStatusBar()
    {
        var view = (SessionTabs.SelectedItem as TabItem)?.Content as TerminalTabView;
        if (view is null)
        {
            StatusDot.Fill = _mutedBrush;
            StatusText.Text = "就绪";
            StatusRightText.Text = $"{CountSessions(_store.Roots)} 个会话";
            SessionHintText.Text = "双击会话开始连接";
            Title = "KiteShell";
            return;
        }

        StatusDot.Fill = view.StateBrush;
        StatusText.Text = view.StateMessage;
        StatusRightText.Text = $"{view.Target}   {view.TerminalSizeText}";
        SessionHintText.Text = view.State switch
        {
            TerminalConnectionState.Connected => "终端已就绪",
            TerminalConnectionState.Connecting => "正在建立连接...",
            TerminalConnectionState.Failed => "连接失败，可点击“重连”重试",
            _ => "会话已断开",
        };
        Title = $"{view.Profile.Name} — KiteShell";
    }

    private string GetTargetGroupPath()
    {
        if (_selectedNode is null)
        {
            return string.Empty;
        }

        if (_selectedNode.IsFolder)
        {
            var parent = _store.GetParentGroupPath(_selectedNode.Node);
            return SessionStore.JoinGroupPath(parent, _selectedNode.Node.Name);
        }

        return _store.GetParentGroupPath(_selectedNode.Node) ?? string.Empty;
    }

    private void OnSessionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedNode = SessionTree.SelectedItem as SessionNodeViewModel;
        if (_selectedNode is null)
        {
            return;
        }

        SessionHintText.Text = _selectedNode.IsFolder
            ? $"分组：{_selectedNode.Name}"
            : $"{_selectedNode.Name} · {_selectedNode.Subtitle}";
    }

    private async void OnSessionTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_selectedNode is { IsFolder: false })
        {
            await OpenSessionAsync(_selectedNode.Node);
        }
    }

    private void OnSessionTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(SessionTree);
        if (!point.Properties.IsRightButtonPressed)
        {
            return;
        }

        if (e.Source is Visual visual
            && visual.FindAncestorOfType<TreeViewItem>() is { DataContext: SessionNodeViewModel vm })
        {
            SessionTree.SelectedItem = vm;
            _selectedNode = vm;
        }
    }

    private void OnSessionSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        LoadTree();
    }

    private void OnSnippetDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SnippetList.SelectedItem is not Snippet snippet)
        {
            return;
        }

        var view = (SessionTabs.SelectedItem as TabItem)?.Content as TerminalTabView;
        if (view is null)
        {
            SessionHintText.Text = "请先打开一个会话";
            return;
        }

        view.SendSnippet(snippet);
    }

    private async void OnAddSnippetClick(object? sender, RoutedEventArgs e)
    {
        var command = await PromptTextAsync("添加快捷命令", "输入要发送到终端的命令，例如：df -h");
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var trimmed = command.Trim();
        _snippetStore.Items.Add(new Snippet
        {
            Name = trimmed.Length > 24 ? trimmed[..24] + "…" : trimmed,
            Command = trimmed,
        });
        _snippetStore.Save();
        SnippetList.ItemsSource = null;
        SnippetList.ItemsSource = _snippetStore.Items;
    }

    private void OnDeleteSnippetClick(object? sender, RoutedEventArgs e)
    {
        if (SnippetList.SelectedItem is not Snippet snippet)
        {
            return;
        }

        _snippetStore.Items.Remove(snippet);
        _snippetStore.Save();
        SnippetList.ItemsSource = null;
        SnippetList.ItemsSource = _snippetStore.Items;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (shift && e.Key == Key.N)
        {
            e.Handled = true;
            _ = CreateSessionAsync(GetTargetGroupPath());
        }
        else if (shift && e.Key == Key.F)
        {
            e.Handled = true;
            _ = CreateFolderAsync(GetTargetGroupPath());
        }
        else if (shift && e.Key == Key.W)
        {
            if ((SessionTabs.SelectedItem as TabItem)?.Content is TerminalTabView view)
            {
                e.Handled = true;
                CloseTab(view);
            }
        }
        else if (e.Key == Key.Tab)
        {
            e.Handled = true;
            CycleTab(shift ? -1 : 1);
        }
    }

    private void CycleTab(int delta)
    {
        var count = SessionTabs.ItemCount;
        if (count < 2)
        {
            return;
        }

        var index = SessionTabs.SelectedIndex;
        index = (index + delta + count) % count;
        SessionTabs.SelectedIndex = index;
    }

    private sealed record OpenTab(
        TerminalTabView View,
        TabItem Item,
        Ellipse Dot,
        TextBlock Title);

    private sealed record TabHeader(
        Control Container,
        Ellipse Dot,
        TextBlock Title);
}
