using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using KiteShell.Models;

namespace KiteShell.Services;

public sealed class SnippetStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public List<Snippet> Items { get; private set; } = new();

    public SnippetStore(string path)
    {
        _path = path;
    }

    public void Load()
    {
        var loaded = false;
        try
        {
            if (File.Exists(_path))
            {
                Items = JsonSerializer.Deserialize<List<Snippet>>(File.ReadAllText(_path), _options)
                        ?? new List<Snippet>();
                loaded = true;
            }
        }
        catch
        {
            Items = new List<Snippet>();
        }

        if (!loaded)
        {
            Items.AddRange(DefaultSnippets());
        }
    }

    public void Save()
    {
        AppPaths.EnsureDataDirectory();
        var json = JsonSerializer.Serialize(Items, _options);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }

    private static IEnumerable<Snippet> DefaultSnippets()
    {
        yield return new Snippet { Name = "磁盘使用", Command = "df -h" };
        yield return new Snippet { Name = "内存使用", Command = "free -h" };
        yield return new Snippet { Name = "系统信息", Command = "uname -a" };
        yield return new Snippet { Name = "进程排行", Command = "ps aux --sort=-%cpu | head -20" };
        yield return new Snippet { Name = "监听端口", Command = "ss -tulnp" };
        yield return new Snippet { Name = "当前目录大小", Command = "du -sh ." };
        yield return new Snippet { Name = "网络连通性", Command = "ping -c 4 1.1.1.1" };
    }
}
