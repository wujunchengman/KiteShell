using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KiteShell.Models;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace KiteShell.Services;

/// <summary>
/// SFTP 文件服务。使用独立连接，与终端 Shell 互不影响。
/// </summary>
public sealed class SftpService : IDisposable
{
    private readonly object _gate = new();
    private SftpClient? _client;
    private bool _disposed;

    public string HomePath { get; private set; } = "/";

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

    public async Task ConnectAsync(ConnectionSettings settings, SshInteraction interaction)
    {
        await Task.Run(() =>
        {
            Disconnect();

            var connectionInfo = SshConnectionFactory.Build(settings, interaction);
            var client = new SftpClient(connectionInfo)
            {
                OperationTimeout = TimeSpan.FromSeconds(30),
            };
            SshConnectionFactory.AttachHostKeyVerification(client, interaction);

            try
            {
                client.Connect();
                string home;
                try
                {
                    client.ChangeDirectory(".");
                    home = string.IsNullOrWhiteSpace(client.WorkingDirectory) ? "/" : client.WorkingDirectory;
                }
                catch
                {
                    home = "/";
                }

                lock (_gate)
                {
                    if (_disposed)
                    {
                        client.Dispose();
                        throw new ObjectDisposedException(nameof(SftpService));
                    }

                    _client = client;
                    HomePath = home;
                }
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }).ConfigureAwait(false);
    }

    public Task<List<RemoteEntry>> ListAsync(string path)
    {
        return Task.Run(() =>
        {
            var client = GetClient();
            var entries = new List<RemoteEntry>();
            foreach (var file in client.ListDirectory(path))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }

                entries.Add(new RemoteEntry
                {
                    Name = file.Name,
                    FullPath = JoinRemotePath(path, file.Name),
                    IsDirectory = file.IsDirectory,
                    IsSymbolicLink = file.IsSymbolicLink,
                    Size = file.IsDirectory ? 0 : file.Length,
                    Modified = file.LastWriteTimeUtc.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc).ToLocalTime()
                        : file.LastWriteTimeUtc.ToLocalTime(),
                    PermissionsText = FormatPermissions(file),
                });
            }

            return entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    public Task UploadAsync(Stream source, string remotePath)
    {
        return Task.Run(() =>
        {
            var client = GetClient();
            client.UploadFile(source, remotePath, canOverride: true);
        });
    }

    public Task DownloadAsync(string remotePath, Stream destination)
    {
        return Task.Run(() =>
        {
            var client = GetClient();
            client.DownloadFile(remotePath, destination);
        });
    }

    public Task CreateDirectoryAsync(string path)
    {
        return Task.Run(() => GetClient().CreateDirectory(path));
    }

    public Task RenameAsync(string oldPath, string newPath)
    {
        return Task.Run(() => GetClient().RenameFile(oldPath, newPath));
    }

    public Task DeleteAsync(RemoteEntry entry)
    {
        return Task.Run(() =>
        {
            var client = GetClient();
            if (entry.IsDirectory && !entry.IsSymbolicLink)
            {
                DeleteDirectoryRecursive(client, entry.FullPath);
            }
            else
            {
                client.DeleteFile(entry.FullPath);
            }
        });
    }

    public Task<bool> ExistsAsync(string path)
    {
        return Task.Run(() => GetClient().Exists(path));
    }

    public string NormalizePath(string current, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            input = current;
        }

        input = input.Trim();
        string combined;
        if (input == "~")
        {
            combined = HomePath;
        }
        else if (input.StartsWith("~/", StringComparison.Ordinal))
        {
            combined = JoinRemotePath(HomePath, input[2..]);
        }
        else if (input.StartsWith('/'))
        {
            combined = input;
        }
        else
        {
            combined = JoinRemotePath(current, input);
        }

        var stack = new List<string>();
        foreach (var segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(segment);
        }

        return "/" + string.Join('/', stack);
    }

    public static string JoinRemotePath(string directory, string name)
    {
        if (string.IsNullOrEmpty(directory) || directory == "/")
        {
            return "/" + name.TrimStart('/');
        }

        return directory.TrimEnd('/') + "/" + name.TrimStart('/');
    }

    public void Disconnect()
    {
        SftpClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
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
            // 忽略断开异常。
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

    private SftpClient GetClient()
    {
        lock (_gate)
        {
            if (_client is { IsConnected: true })
            {
                return _client;
            }
        }

        throw new InvalidOperationException("SFTP 连接未就绪。");
    }

    private static void DeleteDirectoryRecursive(SftpClient client, string path)
    {
        foreach (var child in client.ListDirectory(path))
        {
            if (child.Name is "." or "..")
            {
                continue;
            }

            if (child.IsDirectory && !child.IsSymbolicLink)
            {
                DeleteDirectoryRecursive(client, child.FullName);
            }
            else
            {
                client.DeleteFile(child.FullName);
            }
        }

        client.DeleteDirectory(path);
    }

    private static string FormatPermissions(ISftpFile file)
    {
        try
        {
            var chars = new char[10];
            chars[0] = file.IsDirectory ? 'd' : file.IsSymbolicLink ? 'l' : '-';
            chars[1] = file.OwnerCanRead ? 'r' : '-';
            chars[2] = file.OwnerCanWrite ? 'w' : '-';
            chars[3] = file.OwnerCanExecute ? 'x' : '-';
            chars[4] = file.GroupCanRead ? 'r' : '-';
            chars[5] = file.GroupCanWrite ? 'w' : '-';
            chars[6] = file.GroupCanExecute ? 'x' : '-';
            chars[7] = file.OthersCanRead ? 'r' : '-';
            chars[8] = file.OthersCanWrite ? 'w' : '-';
            chars[9] = file.OthersCanExecute ? 'x' : '-';
            return new string(chars);
        }
        catch
        {
            return file.IsDirectory ? "d---------" : "----------";
        }
    }
}
