using System;

namespace KiteShell.Models;

public sealed class Snippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public bool AppendEnter { get; set; } = true;

    public Snippet Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Name,
        Command = Command,
        AppendEnter = AppendEnter,
    };
}
