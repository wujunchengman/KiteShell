using System;

namespace KiteShell.Models;

public sealed class RemoteEntry
{
    public string Name { get; init; } = string.Empty;

    public string FullPath { get; init; } = string.Empty;

    public bool IsDirectory { get; init; }

    public bool IsSymbolicLink { get; init; }

    public long Size { get; init; }

    public DateTime Modified { get; init; }

    public string SizeText => IsDirectory ? "—" : FormatSize(Size);

    public string ModifiedText => Modified == default ? "—" : Modified.ToString("yyyy-MM-dd HH:mm");

    public string PermissionsText { get; init; } = string.Empty;

    public string IconKind => IsDirectory ? "folder" : "file";

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
