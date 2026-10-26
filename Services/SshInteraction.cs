using System;
using KiteShell.Models;

namespace KiteShell.Services;

public sealed record HostKeyPromptInfo(
    string Host,
    int Port,
    string KeyType,
    string Fingerprint,
    bool Changed);

public enum HostKeyAction
{
    Reject,
    TrustOnce,
    TrustAndSave,
}

public sealed record AuthenticationPromptInfo(
    string Instruction,
    string Prompt,
    bool Echo);

/// <summary>
/// 连接期间与用户交互的同步回调。回调可能运行在后台线程，实现方需自行切换到 UI 线程。
/// </summary>
public sealed class SshInteraction
{
    public required SessionNode Profile { get; init; }

    public SessionSecrets Secrets { get; set; } = new();

    public KnownHostsStore? KnownHosts { get; init; }

    public Func<HostKeyPromptInfo, HostKeyAction>? VerifyHostKey { get; set; }

    public Func<AuthenticationPromptInfo, string?>? Prompt { get; set; }
}
