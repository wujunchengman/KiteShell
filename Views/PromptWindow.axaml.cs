using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace KiteShell.Views;

public sealed record PasswordPromptResult(string Password, bool Remember);

public partial class PromptWindow : Window
{
    private Mode _mode;

    public PromptWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (_mode == Mode.Confirm)
            {
                OkButton.Focus();
            }
            else
            {
                InputBox.Focus();
                InputBox.SelectAll();
            }
        };
    }

    public string? TextValue { get; private set; }

    public bool Remember { get; private set; }

    public static async Task<string?> AskAsync(
        Window owner,
        string title,
        string message,
        string? initial = null,
        bool password = false,
        string okText = "确定",
        string? watermark = null)
    {
        var dialog = new PromptWindow();
        dialog.ConfigureAsk(title, message, initial, password, okText, watermark);
        var confirmed = await dialog.ShowDialog<bool>(owner);
        return confirmed ? dialog.TextValue : null;
    }

    public static async Task<PasswordPromptResult?> AskPasswordAsync(
        Window owner,
        string title,
        string message,
        bool showRemember = true)
    {
        var dialog = new PromptWindow();
        dialog.ConfigureAsk(title, message, null, password: true, okText: "连接", watermark: "密码");
        dialog._mode = Mode.Password;
        dialog.RememberCheck.IsVisible = showRemember;
        dialog.InputBox.PasswordChar = '●';
        var confirmed = await dialog.ShowDialog<bool>(owner);
        return confirmed ? new PasswordPromptResult(dialog.TextValue ?? string.Empty, dialog.Remember) : null;
    }

    public static async Task<bool> ConfirmAsync(
        Window owner,
        string title,
        string message,
        string okText = "确定",
        bool danger = false)
    {
        var dialog = new PromptWindow();
        dialog.ConfigureConfirm(title, message, okText, danger);
        return await dialog.ShowDialog<bool>(owner);
    }

    public static async Task ShowMessageAsync(Window owner, string title, string message)
    {
        var dialog = new PromptWindow();
        dialog.ConfigureMessage(title, message);
        await dialog.ShowDialog<bool>(owner);
    }

    private void ConfigureAsk(
        string title,
        string message,
        string? initial,
        bool password,
        string okText,
        string? watermark)
    {
        _mode = password ? Mode.Password : Mode.Text;
        HeaderText.Text = title;
        MessageText.Text = message;
        MessageText.IsVisible = !string.IsNullOrWhiteSpace(message);
        InputBox.Text = initial ?? string.Empty;
        InputBox.PlaceholderText = watermark;
        InputBox.IsVisible = true;
        if (password)
        {
            InputBox.PasswordChar = '●';
        }

        OkButton.Content = okText;
        Title = title;
    }

    private void ConfigureConfirm(string title, string message, string okText, bool danger)
    {
        _mode = Mode.Confirm;
        HeaderText.Text = title;
        MessageText.Text = message;
        MessageText.IsVisible = true;
        InputBox.IsVisible = false;
        OkButton.Content = okText;
        CancelButton.Content = "取消";
        if (danger)
        {
            OkButton.Background = new SolidColorBrush(Color.Parse("#C93C37"));
            OkButton.Foreground = Brushes.White;
        }

        Title = title;
    }

    private void ConfigureMessage(string title, string message)
    {
        _mode = Mode.Message;
        HeaderText.Text = title;
        MessageText.Text = message;
        MessageText.IsVisible = true;
        InputBox.IsVisible = false;
        CancelButton.IsVisible = false;
        OkButton.Content = "关闭";
        Title = title;
    }

    private void OnOkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ConfirmCore();

    private void ConfirmCore()
    {
        if (_mode is Mode.Text or Mode.Password)
        {
            TextValue = InputBox.Text ?? string.Empty;
            Remember = RememberCheck.IsChecked == true;
        }

        Close(true);
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(false);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ConfirmCore();
        }
    }

    private enum Mode
    {
        Text,
        Password,
        Confirm,
        Message,
    }
}
