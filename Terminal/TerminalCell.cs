using System;

namespace KiteShell.Terminal;

[Flags]
public enum CellFlags
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Hidden = 1 << 6,
    Strike = 1 << 7,
}

public struct TerminalCell
{
    /// <summary>Unicode 码点，0 表示空白。</summary>
    public int Codepoint;

    /// <summary>宽字符占用的第二个单元格。</summary>
    public bool Continuation;

    public TermColor Fg;

    public TermColor Bg;

    public CellFlags Flags;

    public readonly bool IsEmpty => Codepoint == 0 && !Continuation;

    public static TerminalCell Blank(TermColor background) => new()
    {
        Codepoint = 0,
        Bg = background,
    };
}
