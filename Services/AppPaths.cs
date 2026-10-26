using System;
using System.IO;

namespace KiteShell.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KiteShell");

    public static string SessionsFile => Path.Combine(DataDirectory, "sessions.json");

    public static string KnownHostsFile => Path.Combine(DataDirectory, "known_hosts.json");

    public static string SecretKeyFile => Path.Combine(DataDirectory, "secret.key");

    public static string SnippetsFile => Path.Combine(DataDirectory, "snippets.json");

    public static string EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(
                    DataDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // 尽力而为；文件系统可能不支持 Unix 权限。
            }
        }

        return DataDirectory;
    }
}
