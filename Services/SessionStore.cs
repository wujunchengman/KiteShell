using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using KiteShell.Models;

namespace KiteShell.Services;

public sealed class SessionStore
{
    private static readonly JsonTypeInfo<SessionFile> SessionFileTypeInfo =
        AppJson.TypeInfo<SessionFile>();

    private readonly string _path;

    public List<SessionNode> Roots { get; private set; } = new();

    public SessionStore(string path)
    {
        _path = path;
    }

    public void Load()
    {
        Roots = new List<SessionNode>();
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var document = JsonSerializer.Deserialize(json, SessionFileTypeInfo);
                if (document?.Nodes is not null)
                {
                    Roots = document.Nodes;
                    Sanitize(Roots);
                }
            }
        }
        catch
        {
            // 文件损坏时以空列表启动，并在首次保存时覆盖（保留 .bak 备份）。
            TryBackupBrokenFile();
        }
    }

    public void Save()
    {
        AppPaths.EnsureDataDirectory();
        var document = new SessionFile { Version = 1, Nodes = Roots };
        var json = JsonSerializer.Serialize(document, SessionFileTypeInfo);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // 忽略。
            }
        }

        File.Move(temp, _path, overwrite: true);
    }

    public IEnumerable<string> GetGroupPaths()
    {
        var paths = new List<string>();
        void Walk(IEnumerable<SessionNode> nodes, string prefix)
        {
            foreach (var node in nodes)
            {
                if (!node.IsFolder)
                {
                    continue;
                }

                var path = string.IsNullOrEmpty(prefix) ? node.Name : $"{prefix}/{node.Name}";
                paths.Add(path);
                Walk(node.Children, path);
            }
        }

        Walk(Roots, string.Empty);
        return paths;
    }

    public string? GetParentGroupPath(SessionNode target)
    {
        string? found = null;
        void Walk(IEnumerable<SessionNode> nodes, string prefix)
        {
            foreach (var node in nodes)
            {
                if (ReferenceEquals(node, target) || node.Id == target.Id)
                {
                    found = prefix;
                    return;
                }

                var path = string.IsNullOrEmpty(prefix) ? node.Name : $"{prefix}/{node.Name}";
                Walk(node.Children, path);
            }
        }

        Walk(Roots, string.Empty);
        return found;
    }

    public SessionNode? FindById(string id)
    {
        SessionNode? result = null;
        void Walk(IEnumerable<SessionNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (result is not null)
                {
                    return;
                }

                if (node.Id == id)
                {
                    result = node;
                    return;
                }

                Walk(node.Children);
            }
        }

        Walk(Roots);
        return result;
    }

    /// <summary>
    /// 将节点插入到指定分组路径（自动创建缺失的文件夹），若节点已存在则先移出原位置。
    /// </summary>
    public void PlaceNode(SessionNode node, string? groupPath)
    {
        RemoveReference(node.Id);

        var segments = SplitGroupPath(groupPath);
        var list = Roots;
        var path = string.Empty;
        foreach (var segment in segments)
        {
            path = string.IsNullOrEmpty(path) ? segment : $"{path}/{segment}";
            var folder = list.FirstOrDefault(n => n.IsFolder && string.Equals(n.Name, segment, StringComparison.Ordinal));
            if (folder is null)
            {
                folder = new SessionNode
                {
                    Name = segment,
                    Type = SessionNodeType.Folder,
                };
                list.Add(folder);
            }

            list = folder.Children;
        }

        list.Add(node);
    }

    public bool Remove(SessionNode node)
    {
        var removed = RemoveReference(node.Id, removeSelf: true);
        return removed;
    }

    public void RenameFolder(SessionNode folder, string name)
    {
        folder.Name = name.Trim();
    }

    public static string[] SplitGroupPath(string? groupPath)
    {
        if (string.IsNullOrWhiteSpace(groupPath))
        {
            return Array.Empty<string>();
        }

        return groupPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string JoinGroupPath(string? parent, string name)
    {
        return string.IsNullOrWhiteSpace(parent) ? name : $"{parent}/{name}";
    }

    private bool RemoveReference(string id, bool removeSelf = false)
    {
        bool Walk(List<SessionNode> nodes, string? unused = null)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.Id == id)
                {
                    if (removeSelf)
                    {
                        nodes.RemoveAt(i);
                    }

                    return true;
                }

                if (Walk(node.Children))
                {
                    return true;
                }
            }

            return false;
        }

        return Walk(Roots);
    }

    private void Sanitize(IEnumerable<SessionNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.Children ??= new List<SessionNode>();
            node.Name = string.IsNullOrWhiteSpace(node.Name)
                ? (node.IsFolder ? "未命名分组" : "未命名会话")
                : node.Name;
            node.Port = node.Port is > 0 and <= 65535 ? node.Port : 22;
            node.Accent = SessionNode.NormalizeAccent(node.Accent);
            Sanitize(node.Children);
        }
    }

    private void TryBackupBrokenFile()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Copy(_path, _path + ".bak", overwrite: true);
            }
        }
        catch
        {
            // 忽略。
        }
    }

}

internal sealed class SessionFile
{
    public int Version { get; set; }

    public List<SessionNode> Nodes { get; set; } = new();
}
