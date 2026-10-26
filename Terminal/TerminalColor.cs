using System;

namespace KiteShell.Terminal;

/// <summary>
/// 终端颜色。Value 为 0 表示使用主题默认色，否则为 0xFFRRGGBB。
/// </summary>
public readonly struct TermColor : IEquatable<TermColor>
{
    public uint Value { get; }

    private TermColor(uint value)
    {
        Value = value;
    }

    public bool IsDefault => Value == 0;

    public static TermColor Default => default;

    public static TermColor FromRgb(byte r, byte g, byte b) =>
        new(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b);

    public static TermColor FromIndex(int index)
    {
        if (index is >= 0 and < 16)
        {
            return new TermColor(TerminalPalette.Ansi[index]);
        }

        if (index is >= 16 and < 232)
        {
            var cube = index - 16;
            var levels = new byte[] { 0, 95, 135, 175, 215, 255 };
            var r = levels[cube / 36];
            var g = levels[(cube / 6) % 6];
            var b = levels[cube % 6];
            return FromRgb(r, g, b);
        }

        if (index is >= 232 and < 256)
        {
            var level = (byte)(8 + (index - 232) * 10);
            return FromRgb(level, level, level);
        }

        return Default;
    }

    public bool Equals(TermColor other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is TermColor other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(TermColor left, TermColor right) => left.Equals(right);

    public static bool operator !=(TermColor left, TermColor right) => !left.Equals(right);
}

public static class TerminalPalette
{
    // 参考 One Dark / VS Code 暗色终端，兼顾对比度。
    public static readonly uint[] Ansi =
    {
        0xFF23262F, // black
        0xFFE06C75, // red
        0xFF98C379, // green
        0xFFE5C07B, // yellow
        0xFF61AFEF, // blue
        0xFFC678DD, // magenta
        0xFF56B6C2, // cyan
        0xFFC8CCD4, // white
        0xFF5C6370, // bright black
        0xFFFF7B86, // bright red
        0xFFB5E890, // bright green
        0xFFFFD68A, // bright yellow
        0xFF7CC4FF, // bright blue
        0xFFDFA6FF, // bright magenta
        0xFF7FE3EA, // bright cyan
        0xFFFFFFFF, // bright white
    };

    public const uint DefaultForeground = 0xFFC9D1D9;

    public const uint DefaultBackground = 0xFF12141C;

    public const uint Cursor = 0xFF7AA2F7;

    public const uint Selection = 0x773B6FB4;

    public const uint ScrollbarThumb = 0x66A0A8BC;

    public const uint ScrollbarThumbHover = 0x99A0A8BC;
}
