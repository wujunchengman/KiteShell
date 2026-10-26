using System;
using Avalonia.Input;

namespace KiteShell.Terminal;

public static class TerminalKeyEncoder
{
    /// <summary>
    /// 将 Avalonia 按键转换为 xterm 输入序列。可打印字符返回 null，由 TextInput 处理。
    /// </summary>
    public static string? Encode(Key key, KeyModifiers modifiers, bool applicationCursorKeys)
    {
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

        if (ctrl && TryGetControlCode(key, out var controlCode))
        {
            var text = ((char)controlCode).ToString();
            return alt ? "\u001B" + text : text;
        }

        string? Sequence(string final, bool ss3WhenPlain = false)
        {
            if (modifier == 1)
            {
                return applicationCursorKeys || ss3WhenPlain
                    ? $"\u001BO{final}"
                    : $"\u001B[{final}";
            }

            return $"\u001B[1;{modifier}{final}";
        }

        string Tilde(int number)
        {
            return modifier == 1 ? $"\u001B[{number}~" : $"\u001B[{number};{modifier}~";
        }

        string? sequence = key switch
        {
            Key.Up => Sequence("A"),
            Key.Down => Sequence("B"),
            Key.Right => Sequence("C"),
            Key.Left => Sequence("D"),
            Key.Home => Sequence("H"),
            Key.End => Sequence("F"),
            Key.Insert => Tilde(2),
            Key.Delete => Tilde(3),
            Key.PageUp => Tilde(5),
            Key.PageDown => Tilde(6),
            Key.Back => alt ? "\u001B\u007F" : "\u007F",
            Key.Enter => alt ? "\u001B\r" : "\r",
            Key.Tab => shift ? "\u001B[Z" : (alt ? "\u001B\t" : "\t"),
            Key.Escape => alt ? "\u001B\u001B" : "\u001B",
            Key.F1 => FunctionKey("P"),
            Key.F2 => FunctionKey("Q"),
            Key.F3 => FunctionKey("R"),
            Key.F4 => FunctionKey("S"),
            Key.F5 => Tilde(15),
            Key.F6 => Tilde(17),
            Key.F7 => Tilde(18),
            Key.F8 => Tilde(19),
            Key.F9 => Tilde(20),
            Key.F10 => Tilde(21),
            Key.F11 => Tilde(23),
            Key.F12 => Tilde(24),
            _ => null,
        };

        if (sequence is null && alt && !ctrl && TryGetAltCharacter(key, out var character))
        {
            return "\u001B" + character;
        }

        return sequence;

        string FunctionKey(string final)
        {
            if (modifier == 1)
            {
                return "\u001BO" + final;
            }

            return $"\u001B[1;{modifier}{final}";
        }
    }

    private static bool TryGetControlCode(Key key, out int code)
    {
        code = -1;
        if (key is >= Key.A and <= Key.Z)
        {
            code = key - Key.A + 1;
            return true;
        }

        switch (key)
        {
            case Key.Space:
                code = 0;
                return true;
            case Key.OemOpenBrackets:
                code = 27;
                return true;
            case Key.OemBackslash:
                code = 28;
                return true;
            case Key.OemCloseBrackets:
                code = 29;
                return true;
            case Key.OemMinus:
                code = 31;
                return true;
            case Key.OemQuestion:
                code = 127;
                return true;
            case Key.Back:
                code = 8;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetAltCharacter(Key key, out char character)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            character = (char)('a' + (key - Key.A));
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            character = (char)('0' + (key - Key.D0));
            return true;
        }

        character = key switch
        {
            Key.OemMinus => '-',
            Key.OemPlus => '=',
            Key.OemComma => ',',
            Key.OemPeriod => '.',
            Key.OemQuestion => '/',
            Key.OemSemicolon => ';',
            Key.OemQuotes => '\'',
            Key.OemOpenBrackets => '[',
            Key.OemCloseBrackets => ']',
            Key.OemPipe => '\\',
            Key.OemTilde => '`',
            _ => '\0',
        };

        return character != '\0';
    }
}
