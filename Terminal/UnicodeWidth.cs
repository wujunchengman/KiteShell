namespace KiteShell.Terminal;

public static class UnicodeWidth
{
    /// <summary>
    /// 终端单元格宽度：0（组合字符/控制字符）、1（窄字符）、2（CJK/Emoji 等宽字符）。
    /// </summary>
    public static int GetWidth(int codepoint)
    {
        if (codepoint <= 0 || (codepoint < 32) || (codepoint >= 0x7F && codepoint < 0xA0))
        {
            return 0;
        }

        if (IsZeroWidth(codepoint))
        {
            return 0;
        }

        if (IsWide(codepoint))
        {
            return 2;
        }

        return 1;
    }

    private static bool IsZeroWidth(int cp)
    {
        return (cp >= 0x0300 && cp <= 0x036F)
               || (cp >= 0x0483 && cp <= 0x0489)
               || (cp >= 0x0591 && cp <= 0x05BD)
               || (cp >= 0x0610 && cp <= 0x061A)
               || (cp >= 0x064B && cp <= 0x065F)
               || (cp >= 0x0670 && cp <= 0x0670)
               || (cp >= 0x06D6 && cp <= 0x06DC)
               || (cp >= 0x0E31 && cp <= 0x0E31)
               || (cp >= 0x0E34 && cp <= 0x0E3A)
               || (cp >= 0x0E47 && cp <= 0x0E4E)
               || (cp >= 0x1AB0 && cp <= 0x1AFF)
               || (cp >= 0x1DC0 && cp <= 0x1DFF)
               || (cp >= 0x200B && cp <= 0x200F)
               || (cp >= 0x20D0 && cp <= 0x20FF)
               || (cp >= 0xFE00 && cp <= 0xFE0F)
               || (cp >= 0xFE20 && cp <= 0xFE2F)
               || (cp >= 0xE0100 && cp <= 0xE01EF)
               || cp == 0xFEFF;
    }

    private static bool IsWide(int cp)
    {
        return (cp >= 0x1100 && cp <= 0x115F)   // Hangul Jamo
               || cp == 0x2329 || cp == 0x232A
               || (cp >= 0x2E80 && cp <= 0x303E) // CJK 偏旁与标点
               || (cp >= 0x3041 && cp <= 0x33FF) // 假名、CJK 兼容
               || (cp >= 0x3400 && cp <= 0x4DBF) // CJK 扩展 A
               || (cp >= 0x4E00 && cp <= 0x9FFF) // CJK 基本区
               || (cp >= 0xA000 && cp <= 0xA4CF) // 彝文
               || (cp >= 0xA960 && cp <= 0xA97F)
               || (cp >= 0xAC00 && cp <= 0xD7A3) // 韩文音节
               || (cp >= 0xF900 && cp <= 0xFAFF) // CJK 兼容
               || (cp >= 0xFE10 && cp <= 0xFE19)
               || (cp >= 0xFE30 && cp <= 0xFE6F)
               || (cp >= 0xFF00 && cp <= 0xFF60) // 全角
               || (cp >= 0xFFE0 && cp <= 0xFFE6)
               || (cp >= 0x1F000 && cp <= 0x1FAFF) // Emoji / 麻将牌等
               || (cp >= 0x1F300 && cp <= 0x1F64F)
               || (cp >= 0x1F900 && cp <= 0x1F9FF)
               || (cp >= 0x20000 && cp <= 0x3FFFD); // CJK 扩展 B 及以上
    }
}
