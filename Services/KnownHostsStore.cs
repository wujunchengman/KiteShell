using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace KiteShell.Services;

public enum KnownHostStatus
{
    Unknown,
    Match,
    Mismatch,
}

public sealed class KnownHostEntry
{
    public string KeyType { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 类似 OpenSSH known_hosts 的主机指纹信任库（首次使用需确认，指纹变化会告警）。
/// </summary>
public sealed class KnownHostsStore
{
    private static readonly JsonTypeInfo<Dictionary<string, KnownHostEntry>> EntriesTypeInfo =
        AppJson.TypeInfo<Dictionary<string, KnownHostEntry>>();

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, KnownHostEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public KnownHostsStore(string path)
    {
        _path = path;
        Load();
    }

    public KnownHostStatus Check(string host, int port, string keyType, string fingerprint)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(Key(host, port), out var entry))
            {
                return KnownHostStatus.Unknown;
            }

            return string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? KnownHostStatus.Match
                : KnownHostStatus.Mismatch;
        }
    }

    public void Trust(string host, int port, string keyType, string fingerprint)
    {
        lock (_gate)
        {
            _entries[Key(host, port)] = new KnownHostEntry
            {
                KeyType = keyType,
                Fingerprint = fingerprint,
                AddedUtc = DateTime.UtcNow,
            };
            SaveCore();
        }
    }

    private static string Key(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var entries = JsonSerializer.Deserialize(
                File.ReadAllText(_path), EntriesTypeInfo);
            if (entries is null)
            {
                return;
            }

            foreach (var (key, value) in entries)
            {
                _entries[key] = value;
            }
        }
        catch
        {
            // 信任库损坏时按空库处理。
        }
    }

    private void SaveCore()
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, EntriesTypeInfo));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // 信任状态保存失败不应中断连接，仅本次会话内生效。
        }
    }
}
