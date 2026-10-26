using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KiteShell.Services;

public sealed record ImportedSshSession(
    string Alias,
    string Host,
    int Port,
    string Username,
    string? IdentityFile);

/// <summary>
/// 解析 OpenSSH 客户端配置中的 Host 块，导入常用连接字段。
/// 通配 Host/Match 块会被跳过，避免把模式误当成主机名。
/// </summary>
public static class SshConfigImporter
{
    public static IReadOnlyList<ImportedSshSession> Parse(string path, string defaultUsername)
    {
        var result = new List<ImportedSshSession>();
        if (!File.Exists(path))
        {
            return result;
        }

        HostBlock? current = null;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = IndexOfWhitespace(line);
            var key = (separator < 0 ? line : line[..separator]).Trim();
            var value = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();
            key = key.ToLowerInvariant();

            if (key == "host")
            {
                Flush(current, defaultUsername, result);
                var aliases = SplitValues(value)
                    .Where(alias => !alias.Contains('*') && !alias.Contains('?') && !alias.StartsWith('!'))
                    .ToList();
                current = aliases.Count > 0 ? new HostBlock(aliases) : null;
                continue;
            }

            if (key == "match")
            {
                Flush(current, defaultUsername, result);
                current = null;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            switch (key)
            {
                case "hostname":
                    current.HostName = Unquote(value);
                    break;
                case "user":
                    current.Username = Unquote(value);
                    break;
                case "port":
                    if (int.TryParse(Unquote(value), out var port) && port is > 0 and <= 65535)
                    {
                        current.Port = port;
                    }

                    break;
                case "identityfile":
                    if (current.IdentityFile is null)
                    {
                        var identity = Unquote(value);
                        if (!string.IsNullOrWhiteSpace(identity)
                            && !identity.Equals("none", StringComparison.OrdinalIgnoreCase))
                        {
                            current.IdentityFile = ExpandTokens(identity, current);
                        }
                    }

                    break;
            }
        }

        Flush(current, defaultUsername, result);
        return result;
    }

    private static void Flush(
        HostBlock? block,
        string defaultUsername,
        List<ImportedSshSession> result)
    {
        if (block is null)
        {
            return;
        }

        var username = string.IsNullOrWhiteSpace(block.Username)
            ? defaultUsername
            : block.Username.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            username = Environment.UserName;
        }

        foreach (var alias in block.Aliases)
        {
            var host = string.IsNullOrWhiteSpace(block.HostName) ? alias : block.HostName.Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            result.Add(new ImportedSshSession(
                alias,
                host,
                block.Port,
                username,
                block.IdentityFile));
        }
    }

    private static IEnumerable<string> SplitValues(string value)
    {
        return value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Unquote)
            .Where(item => item.Length > 0);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string StripComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote == '\0' && (ch == '"' || ch == '\''))
            {
                quote = ch;
            }
            else if (quote != '\0' && ch == quote)
            {
                quote = '\0';
            }
            else if (quote == '\0' && ch == '#')
            {
                return line[..i];
            }
        }

        return line;
    }

    private static int IndexOfWhitespace(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]) || value[i] == '=')
            {
                return i;
            }
        }

        return -1;
    }

    private static string ExpandTokens(string value, HostBlock block)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return value
            .Replace("%d", home, StringComparison.Ordinal)
            .Replace("%h", block.HostName ?? block.Aliases[0], StringComparison.Ordinal)
            .Replace("%r", block.Username ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class HostBlock
    {
        public HostBlock(IEnumerable<string> aliases)
        {
            Aliases = aliases.ToList();
        }

        public List<string> Aliases { get; }

        public string? HostName { get; set; }

        public string? Username { get; set; }

        public int Port { get; set; } = 22;

        public string? IdentityFile { get; set; }
    }
}
