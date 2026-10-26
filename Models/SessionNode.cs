using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace KiteShell.Models;

public enum SessionNodeType
{
    Folder,
    Session,
}

public enum SshAuthMethod
{
    Password,
    PrivateKey,
}

/// <summary>
/// 会话树节点。文件夹节点只使用 Name/Children，会话节点使用其余字段。
/// </summary>
public sealed class SessionNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public SessionNodeType Type { get; set; } = SessionNodeType.Session;

    public List<SessionNode> Children { get; set; } = new();

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string Username { get; set; } = string.Empty;

    public SshAuthMethod Auth { get; set; } = SshAuthMethod.Password;

    public bool SavePassword { get; set; }

    public string? PasswordCipher { get; set; }

    public string? PrivateKeyPath { get; set; }

    public string? PassphraseCipher { get; set; }

    public string? StartupCommand { get; set; }

    public string Accent { get; set; } = AccentColors[0].Hex;

    public DateTime? LastConnectedUtc { get; set; }

    [JsonIgnore]
    public bool IsFolder => Type == SessionNodeType.Folder;

    [JsonIgnore]
    public string Target => IsFolder ? string.Empty : $"{Username}@{Host}:{Port}";

    [JsonIgnore]
    public string DisplaySubtitle => IsFolder ? $"{Children.Count} 项" : Target;

    public SessionNode Clone(bool newIds = true)
    {
        return new SessionNode
        {
            Id = newIds ? Guid.NewGuid().ToString("N") : Id,
            Name = Name,
            Type = Type,
            Children = Children.Select(c => c.Clone(newIds)).ToList(),
            Host = Host,
            Port = Port,
            Username = Username,
            Auth = Auth,
            SavePassword = SavePassword,
            PasswordCipher = PasswordCipher,
            PrivateKeyPath = PrivateKeyPath,
            PassphraseCipher = PassphraseCipher,
            StartupCommand = StartupCommand,
            Accent = Accent,
            LastConnectedUtc = LastConnectedUtc,
        };
    }

    public static IReadOnlyList<AccentColor> AccentColors { get; } = new[]
    {
        new AccentColor("蓝色", "#4C8DFF"),
        new AccentColor("绿色", "#3FB950"),
        new AccentColor("紫色", "#A371F7"),
        new AccentColor("橙色", "#F0883E"),
        new AccentColor("红色", "#F85149"),
        new AccentColor("青色", "#39C5CF"),
    };

    public static string NormalizeAccent(string? accent)
    {
        if (string.IsNullOrWhiteSpace(accent))
        {
            return AccentColors[0].Hex;
        }

        return AccentColors.FirstOrDefault(c => string.Equals(c.Hex, accent, StringComparison.OrdinalIgnoreCase))?.Hex
               ?? AccentColors[0].Hex;
    }
}

public sealed record AccentColor(string Name, string Hex);
