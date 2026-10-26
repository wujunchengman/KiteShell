using System;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace KiteShell.Services;

/// <summary>
/// 一条交互式 SSH Shell 通道。输出在后台线程读取并按 UTF-8 增量解码，
/// 写入与窗口调整通过单消费者队列串行化，保证顺序。
/// </summary>
public sealed class SshSession : IDisposable
{
    private readonly object _gate = new();
    private SshClient? _client;
    private ShellStream? _stream;
    private Channel<OutboundMessage>? _outbox;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private uint _columns = 80;
    private uint _rows = 24;

    public event EventHandler<string>? TextReceived;

    public event EventHandler<string>? Closed;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _client?.IsConnected == true;
            }
        }
    }

    public uint Columns => _columns;

    public uint Rows => _rows;

    public async Task ConnectAsync(
        ConnectionSettings settings,
        SshInteraction interaction,
        uint columns,
        uint rows)
    {
        await Task.Run(() => ConnectCore(settings, interaction, columns, rows)).ConfigureAwait(false);
    }

    public bool Send(string text)
    {
        lock (_gate)
        {
            if (_outbox is null || _cts?.IsCancellationRequested != false)
            {
                return false;
            }

            return _outbox.Writer.TryWrite(OutboundMessage.FromText(text));
        }
    }

    public void Resize(uint columns, uint rows)
    {
        columns = Math.Clamp(columns, 2, 1000);
        rows = Math.Clamp(rows, 2, 1000);

        lock (_gate)
        {
            _columns = columns;
            _rows = rows;
            if (_outbox is not null && _cts?.IsCancellationRequested == false)
            {
                _outbox.Writer.TryWrite(OutboundMessage.FromSize(columns, rows));
            }
        }
    }

    public void Disconnect()
    {
        CancellationTokenSource? cts;
        ShellStream? stream;
        SshClient? client;

        lock (_gate)
        {
            cts = _cts;
            stream = _stream;
            client = _client;
            _outbox?.Writer.TryComplete();
            _outbox = null;
            _stream = null;
            _client = null;
            _cts = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            stream?.Dispose();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            if (client is { IsConnected: true })
            {
                client.Disconnect();
            }

            client?.Dispose();
        }
        catch
        {
            // 忽略。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disconnect();
    }

    private void ConnectCore(
        ConnectionSettings settings,
        SshInteraction interaction,
        uint columns,
        uint rows)
    {
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            throw new InvalidOperationException("主机地址不能为空。");
        }

        if (string.IsNullOrWhiteSpace(settings.Username))
        {
            throw new InvalidOperationException("用户名不能为空。");
        }

        Disconnect();

        var cts = new CancellationTokenSource();
        var connectionInfo = SshConnectionFactory.Build(settings, interaction);
        var client = new SshClient(connectionInfo);
        SshConnectionFactory.AttachHostKeyVerification(client, interaction);
        client.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ShellStream? stream = null;

        try
        {
            client.Connect();

            stream = client.CreateShellStream("xterm-256color", columns, rows, 0, 0, 16 * 1024);
            var outbox = Channel.CreateUnbounded<OutboundMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(SshSession));
                }

                _client = client;
                _stream = stream;
                _outbox = outbox;
                _cts = cts;
                _columns = columns;
                _rows = rows;
            }

            if (!string.IsNullOrWhiteSpace(settings.StartupCommand))
            {
                outbox.Writer.TryWrite(OutboundMessage.FromText(settings.StartupCommand + "\r"));
            }

            _ = Task.Run(() => ReadLoop(stream, cts.Token));
            _ = Task.Run(() => WriteLoop(stream, outbox.Reader, cts.Token));
        }
        catch
        {
            try
            {
                stream?.Dispose();
            }
            catch
            {
                // 忽略。
            }

            client.Dispose();
            cts.Dispose();
            throw;
        }
    }

    private void ReadLoop(ShellStream stream, CancellationToken token)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        string? closeReason = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    closeReason = "服务器关闭了连接。";
                    break;
                }

                var count = decoder.GetChars(buffer, 0, read, chars, 0);
                if (count > 0)
                {
                    TextReceived?.Invoke(this, new string(chars, 0, count));
                }
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                closeReason = ex is SshException
                    ? $"连接中断：{ex.Message}"
                    : "连接已中断。";
            }
        }

        if (!token.IsCancellationRequested)
        {
            Closed?.Invoke(this, closeReason ?? "会话已结束。");
        }
    }

    private static async Task WriteLoop(
        ShellStream stream,
        ChannelReader<OutboundMessage> reader,
        CancellationToken token)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (message.Kind == OutboundKind.Text)
                {
                    var bytes = Encoding.UTF8.GetBytes(message.Text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                else
                {
                    stream.ChangeWindowSize(message.Columns, message.Rows, 0, 0);
                }
            }
        }
        catch
        {
            // 连接关闭或通道完成。
        }
    }

    private enum OutboundKind
    {
        Text,
        WindowSize,
    }

    private readonly record struct OutboundMessage(
        OutboundKind Kind,
        string Text,
        uint Columns,
        uint Rows)
    {
        public static OutboundMessage FromText(string text) => new(OutboundKind.Text, text, 0, 0);

        public static OutboundMessage FromSize(uint columns, uint rows) =>
            new(OutboundKind.WindowSize, string.Empty, columns, rows);
    }
}
