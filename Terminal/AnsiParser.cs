using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KiteShell.Terminal;

/// <summary>
/// 能够处理常见 xterm/VT100 序列的增量解析器（CSI/OSC/SGR/ALT 屏幕等）。
/// </summary>
public sealed class AnsiParser
{
    private readonly TerminalBuffer _buffer;
    private readonly List<int> _params = new();
    private readonly StringBuilder _osc = new();
    private State _state = State.Ground;
    private int _currentParam;
    private bool _hasParam;
    private char _privateMarker;
    private char _intermediate;
    private char _charsetTarget;
    private int _pendingHighSurrogate = -1;
    private bool _g0Graphics;
    private bool _g1Graphics;
    private bool _shiftOut;

    public AnsiParser(TerminalBuffer buffer)
    {
        _buffer = buffer;
    }

    public void Reset()
    {
        _state = State.Ground;
        _params.Clear();
        _osc.Clear();
        _hasParam = false;
        _currentParam = 0;
        _privateMarker = '\0';
        _intermediate = '\0';
        _pendingHighSurrogate = -1;
        _g0Graphics = false;
        _g1Graphics = false;
        _shiftOut = false;
    }

    public void Feed(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var ch in text)
        {
            HandleChar(ch);
        }

        // 代理对可能被拆分在两个数据块中，保留到下一块继续组合。
        _buffer.NotifyChanged();
    }

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        OscEscape,
        IgnoreString,
        IgnoreStringEscape,
    }

    private void HandleChar(char ch)
    {
        switch (_state)
        {
            case State.Ground:
                HandleGround(ch);
                break;
            case State.Escape:
                HandleEscape(ch);
                break;
            case State.EscapeIntermediate:
                if (_charsetTarget == '0')
                {
                    _g0Graphics = ch == '0';
                }
                else if (_charsetTarget == '1')
                {
                    _g1Graphics = ch == '0';
                }

                _charsetTarget = '\0';
                _state = State.Ground;
                break;
            case State.Csi:
                HandleCsi(ch);
                break;
            case State.Osc:
                HandleOscChar(ch);
                break;
            case State.OscEscape:
                if (ch == '\\')
                {
                    FinishOsc();
                }
                else
                {
                    _state = State.Ground;
                    HandleChar(ch);
                }

                break;
            case State.IgnoreString:
                if (ch == 0x1B)
                {
                    _state = State.IgnoreStringEscape;
                }

                break;
            case State.IgnoreStringEscape:
                if (ch == '\\')
                {
                    _state = State.Ground;
                }
                else
                {
                    _state = State.IgnoreString;
                }

                break;
        }
    }

    private void HandleGround(char ch)
    {
        if (char.IsSurrogate(ch))
        {
            HandleSurrogate(ch);
            return;
        }

        if (_pendingHighSurrogate >= 0)
        {
            _pendingHighSurrogate = -1;
            Print(0xFFFD);
        }

        switch (ch)
        {
            case '\u001B':
                _state = State.Escape;
                _privateMarker = '\0';
                _intermediate = '\0';
                break;
            case '\u009B':
                EnterCsi();
                break;
            case '\u009D':
                BeginOsc();
                break;
            case '\u0090':
            case '\u0098':
            case '\u009E':
            case '\u009F':
                _state = State.IgnoreString;
                break;
            case '\u0084':
                _buffer.LineFeed();
                break;
            case '\u0085':
                _buffer.CarriageReturn();
                _buffer.LineFeed();
                break;
            case '\u008D':
                _buffer.ReverseIndex();
                break;
            case '\u009C':
                break;
            default:
                if (ch < 0x20 || ch == 0x7F)
                {
                    HandleControl(ch);
                }
                else
                {
                    Print(ch);
                }

                break;
        }
    }

    private void HandleSurrogate(char ch)
    {
        if (char.IsHighSurrogate(ch))
        {
            if (_pendingHighSurrogate >= 0)
            {
                Print(0xFFFD);
            }

            _pendingHighSurrogate = ch;
            return;
        }

        if (_pendingHighSurrogate >= 0 && char.IsSurrogatePair((char)_pendingHighSurrogate, ch))
        {
            var codepoint = char.ConvertToUtf32((char)_pendingHighSurrogate, ch);
            _pendingHighSurrogate = -1;
            Print(codepoint);
            return;
        }

        _pendingHighSurrogate = -1;
        Print(0xFFFD);
    }

    private void HandleControl(char ch)
    {
        switch (ch)
        {
            case '\a':
                _buffer.RingBell();
                break;
            case '\b':
                _buffer.Backspace();
                break;
            case '\t':
                _buffer.Tab();
                break;
            case '\n':
            case '\v':
            case '\f':
                _buffer.LineFeed();
                break;
            case '\r':
                _buffer.CarriageReturn();
                break;
            case '\u000E':
                _shiftOut = true;
                break;
            case '\u000F':
                _shiftOut = false;
                break;
            case '\u0018':
            case '\u001A':
                _state = State.Ground;
                break;
        }
    }

    private void Print(int codepoint)
    {
        if (codepoint < 0x20)
        {
            HandleControl((char)codepoint);
            return;
        }

        var graphics = _shiftOut ? _g1Graphics : _g0Graphics;
        if (graphics && codepoint is >= 0x5F and <= 0x7E)
        {
            codepoint = MapLineDrawing((char)codepoint);
        }

        var width = UnicodeWidth.GetWidth(codepoint);
        if (width > 0)
        {
            _buffer.WriteChar(codepoint, width);
        }
    }

    private static int MapLineDrawing(char ch)
    {
        return ch switch
        {
            '`' => 0x25C6,
            'a' => 0x2592,
            'f' => 0x00B0,
            'g' => 0x00B1,
            'j' => 0x2518,
            'k' => 0x2510,
            'l' => 0x250C,
            'm' => 0x2514,
            'n' => 0x253C,
            'q' => 0x2500,
            't' => 0x251C,
            'u' => 0x2524,
            'v' => 0x2534,
            'w' => 0x252C,
            'x' => 0x2502,
            '~' => 0x00B7,
            _ => ch,
        };
    }

    private void HandleEscape(char ch)
    {
        switch (ch)
        {
            case '[':
                EnterCsi();
                return;
            case ']':
                BeginOsc();
                return;
            case 'P':
                _state = State.IgnoreString;
                return;
            case 'X':
            case '^':
            case '_':
                _state = State.IgnoreString;
                return;
            case '7':
                _buffer.SaveCursor();
                break;
            case '8':
                _buffer.RestoreCursor();
                break;
            case 'D':
                _buffer.LineFeed();
                break;
            case 'E':
                _buffer.CarriageReturn();
                _buffer.LineFeed();
                break;
            case 'M':
                _buffer.ReverseIndex();
                break;
            case 'c':
                _buffer.FullReset();
                _g0Graphics = false;
                _g1Graphics = false;
                _shiftOut = false;
                break;
            case '=':
                _buffer.ApplicationKeypad = true;
                break;
            case '>':
                _buffer.ApplicationKeypad = false;
                break;
            case '(':
                _charsetTarget = '0';
                _state = State.EscapeIntermediate;
                return;
            case ')':
                _charsetTarget = '1';
                _state = State.EscapeIntermediate;
                return;
            case '*':
            case '+':
            case '-':
            case '.':
            case '/':
            case '%':
            case '#':
                _charsetTarget = '\0';
                _state = State.EscapeIntermediate;
                return;
            case '\\':
                break;
            default:
                if (ch < 0x20)
                {
                    HandleControl(ch);
                }

                break;
        }

        _state = State.Ground;
    }

    private void EnterCsi()
    {
        _state = State.Csi;
        _params.Clear();
        _currentParam = 0;
        _hasParam = false;
        _privateMarker = '\0';
        _intermediate = '\0';
    }

    private void HandleCsi(char ch)
    {
        if (ch >= '0' && ch <= '9')
        {
            _currentParam = Math.Min(_currentParam * 10 + (ch - '0'), 99999);
            _hasParam = true;
            return;
        }

        switch (ch)
        {
            case ';':
            case ':':
                _params.Add(_hasParam ? _currentParam : -1);
                _currentParam = 0;
                _hasParam = false;
                return;
            case '?' when !_hasParam && _params.Count == 0:
            case '>' when !_hasParam && _params.Count == 0:
            case '<' when !_hasParam && _params.Count == 0:
            case '=' when !_hasParam && _params.Count == 0:
                _privateMarker = ch;
                return;
            case '\u001B':
                _state = State.Escape;
                _params.Clear();
                return;
            case '\u0018':
            case '\u001A':
                _state = State.Ground;
                return;
        }

        if (ch is >= '\u0020' and <= '\u002F')
        {
            _intermediate = ch;
            return;
        }

        if (ch is >= '@' and <= '~')
        {
            _params.Add(_hasParam ? _currentParam : -1);
            _state = State.Ground;
            try
            {
                DispatchCsi(ch);
            }
            catch
            {
                // 解析异常不应影响终端连接。
            }
        }
        else if (ch < 0x20)
        {
            HandleControl(ch);
        }
    }

    private void DispatchCsi(char final)
    {
        var p1 = Param(0, -1);
        var count = p1 < 0 ? 1 : Math.Max(1, p1);

        if (_intermediate == '!' && final == 'p')
        {
            SoftReset();
            return;
        }

        switch (final)
        {
            case 'A':
                _buffer.MoveBy(0, -count);
                break;
            case 'B':
            case 'e':
                _buffer.MoveBy(0, count);
                break;
            case 'C':
            case 'a':
                _buffer.MoveBy(count, 0);
                break;
            case 'D':
                _buffer.MoveBy(-count, 0);
                break;
            case 'E':
                _buffer.MoveBy(0, count);
                _buffer.CarriageReturn();
                break;
            case 'F':
                _buffer.MoveBy(0, -count);
                _buffer.CarriageReturn();
                break;
            case 'G':
            case '`':
                _buffer.MoveToColumn(count - 1);
                break;
            case 'd':
                _buffer.MoveToRow(count - 1);
                break;
            case 'H':
            case 'f':
                _buffer.MoveTo(Param(0, 1) - 1, Param(1, 1) - 1);
                break;
            case 'I':
                _buffer.Tab(count);
                break;
            case 'Z':
                _buffer.BackTab(count);
                break;
            case 'J':
                _buffer.EraseInDisplay(Param(0, 0));
                break;
            case 'K':
                _buffer.EraseInLine(Param(0, 0));
                break;
            case 'L':
                _buffer.InsertLines(count);
                break;
            case 'M':
                _buffer.DeleteLines(count);
                break;
            case 'P':
                _buffer.DeleteChars(count);
                break;
            case '@':
                _buffer.InsertChars(count);
                break;
            case 'X':
                _buffer.EraseChars(count);
                break;
            case 'S':
                _buffer.ScrollUp(count);
                break;
            case 'T':
                _buffer.ScrollDown(count);
                break;
            case 'b':
                _buffer.RepeatLastChar(count);
                break;
            case 'm':
                ApplySgr();
                break;
            case 'h':
                SetModes(true);
                break;
            case 'l':
                SetModes(false);
                break;
            case 'r':
                _buffer.SetScrollRegion(Param(0, 1), Param(1, _buffer.Rows));
                break;
            case 's':
                _buffer.SaveCursor();
                break;
            case 'u':
                _buffer.RestoreCursor();
                break;
            case 'n':
                HandleDeviceStatusReport();
                break;
            case 'c':
                HandleDeviceAttributes();
                break;
            case 't':
            case 'q':
            case 'g':
            case 'x':
            case 'j':
            case 'k':
            case 'i':
            case 'z':
            case '}' :
                break;
            default:
                break;
        }
    }

    private void HandleDeviceStatusReport()
    {
        var mode = Param(0, 0);
        if (_privateMarker == '?')
        {
            if (mode == 6)
            {
                _buffer.RequestResponse($"\u001B[?{_buffer.CursorY + 1};{_buffer.CursorX + 1}R");
            }

            return;
        }

        if (mode == 5)
        {
            _buffer.RequestResponse("\u001B[0n");
        }
        else if (mode == 6)
        {
            _buffer.RequestResponse($"\u001B[{_buffer.CursorY + 1};{_buffer.CursorX + 1}R");
        }
    }

    private void HandleDeviceAttributes()
    {
        if (_privateMarker == '>')
        {
            _buffer.RequestResponse("\u001B[>0;276;0c");
        }
        else
        {
            _buffer.RequestResponse("\u001B[?1;2c");
        }
    }

    private void SoftReset()
    {
        _buffer.ResetPen();
        _buffer.AutoWrap = true;
        _buffer.OriginMode = false;
        _buffer.InsertMode = false;
        _buffer.CursorVisible = true;
        _buffer.ApplicationCursorKeys = false;
        _buffer.ApplicationKeypad = false;
        _buffer.ClearPendingWrap();
    }

    private void SetModes(bool enable)
    {
        if (_privateMarker == '?')
        {
            foreach (var mode in _params)
            {
                switch (mode)
                {
                    case 1:
                        _buffer.ApplicationCursorKeys = enable;
                        break;
                    case 6:
                        _buffer.OriginMode = enable;
                        _buffer.MoveTo(0, 0);
                        break;
                    case 7:
                        _buffer.AutoWrap = enable;
                        if (!enable)
                        {
                            _buffer.ClearPendingWrap();
                        }

                        break;
                    case 12:
                        break;
                    case 25:
                        _buffer.CursorVisible = enable;
                        break;
                    case 47:
                    case 1047:
                        if (enable)
                        {
                            _buffer.EnterAltScreen(clear: true, saveCursor: false);
                        }
                        else
                        {
                            _buffer.ExitAltScreen(restoreCursor: false);
                        }

                        break;
                    case 1048:
                        if (enable)
                        {
                            _buffer.SaveCursor();
                        }
                        else
                        {
                            _buffer.RestoreCursor();
                        }

                        break;
                    case 1049:
                        if (enable)
                        {
                            _buffer.EnterAltScreen(clear: true, saveCursor: true);
                        }
                        else
                        {
                            _buffer.ExitAltScreen(restoreCursor: true);
                        }

                        break;
                    case 2004:
                        _buffer.BracketedPaste = enable;
                        break;
                    default:
                        // 鼠标、焦点上报等模式暂不实现。
                        break;
                }
            }
        }
        else
        {
            foreach (var mode in _params)
            {
                switch (mode)
                {
                    case 4:
                        _buffer.InsertMode = enable;
                        break;
                    case 20:
                        _buffer.NewLineMode = enable;
                        break;
                }
            }
        }
    }

    private void ApplySgr()
    {
        if (_params.Count == 0 || (_params.Count == 1 && _params[0] < 0))
        {
            _buffer.ResetPen();
            return;
        }

        for (var i = 0; i < _params.Count; i++)
        {
            var p = _params[i] < 0 ? 0 : _params[i];
            switch (p)
            {
                case 0:
                    _buffer.ResetPen();
                    break;
                case 1:
                    _buffer.PenFlags |= CellFlags.Bold;
                    break;
                case 2:
                    _buffer.PenFlags |= CellFlags.Dim;
                    break;
                case 3:
                    _buffer.PenFlags |= CellFlags.Italic;
                    break;
                case 4:
                case 21:
                    _buffer.PenFlags |= CellFlags.Underline;
                    break;
                case 5:
                case 6:
                    _buffer.PenFlags |= CellFlags.Blink;
                    break;
                case 7:
                    _buffer.PenFlags |= CellFlags.Inverse;
                    break;
                case 8:
                    _buffer.PenFlags |= CellFlags.Hidden;
                    break;
                case 9:
                    _buffer.PenFlags |= CellFlags.Strike;
                    break;
                case 22:
                    _buffer.PenFlags &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;
                case 23:
                    _buffer.PenFlags &= ~CellFlags.Italic;
                    break;
                case 24:
                    _buffer.PenFlags &= ~CellFlags.Underline;
                    break;
                case 25:
                    _buffer.PenFlags &= ~CellFlags.Blink;
                    break;
                case 27:
                    _buffer.PenFlags &= ~CellFlags.Inverse;
                    break;
                case 28:
                    _buffer.PenFlags &= ~CellFlags.Hidden;
                    break;
                case 29:
                    _buffer.PenFlags &= ~CellFlags.Strike;
                    break;
                case >= 30 and <= 37:
                    _buffer.PenFg = TermColor.FromIndex(p - 30);
                    break;
                case 38:
                    i = ReadExtendedColor(i, foreground: true);
                    break;
                case 39:
                    _buffer.PenFg = TermColor.Default;
                    break;
                case >= 40 and <= 47:
                    _buffer.PenBg = TermColor.FromIndex(p - 40);
                    break;
                case 48:
                    i = ReadExtendedColor(i, foreground: false);
                    break;
                case 49:
                    _buffer.PenBg = TermColor.Default;
                    break;
                case 58:
                    i = ReadExtendedColor(i, foreground: true, apply: false);
                    break;
                case 59:
                    break;
                case >= 90 and <= 97:
                    _buffer.PenFg = TermColor.FromIndex(p - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    _buffer.PenBg = TermColor.FromIndex(p - 100 + 8);
                    break;
                default:
                    break;
            }
        }
    }

    private int ReadExtendedColor(int index, bool foreground, bool apply = true)
    {
        var modeIndex = index + 1;
        if (modeIndex >= _params.Count)
        {
            return index;
        }

        var mode = _params[modeIndex];
        if (mode == 5 && modeIndex + 1 < _params.Count)
        {
            var colorIndex = Math.Clamp(_params[modeIndex + 1], 0, 255);
            if (apply)
            {
                SetPenColor(foreground, TermColor.FromIndex(colorIndex));
            }

            return modeIndex + 1;
        }

        if (mode == 2)
        {
            var offset = modeIndex + 1;
            if (offset < _params.Count && _params[offset] < 0)
            {
                offset++;
            }

            if (offset + 2 < _params.Count)
            {
                var r = (byte)Math.Clamp(_params[offset], 0, 255);
                var g = (byte)Math.Clamp(_params[offset + 1], 0, 255);
                var b = (byte)Math.Clamp(_params[offset + 2], 0, 255);
                if (apply)
                {
                    SetPenColor(foreground, TermColor.FromRgb(r, g, b));
                }

                return offset + 2;
            }
        }

        return index;
    }

    private void SetPenColor(bool foreground, TermColor color)
    {
        if (foreground)
        {
            _buffer.PenFg = color;
        }
        else
        {
            _buffer.PenBg = color;
        }
    }

    private int Param(int index, int defaultValue)
    {
        if (index < _params.Count && _params[index] >= 0)
        {
            return _params[index];
        }

        return defaultValue;
    }

    private void BeginOsc()
    {
        _osc.Clear();
        _state = State.Osc;
    }

    private void HandleOscChar(char ch)
    {
        if (ch == '\a')
        {
            FinishOsc();
            return;
        }

        if (ch == '\u001B')
        {
            _state = State.OscEscape;
            return;
        }

        if (ch == '\r' || ch == '\n')
        {
            return;
        }

        if (_osc.Length < 4096)
        {
            _osc.Append(ch);
        }
    }

    private void FinishOsc()
    {
        _state = State.Ground;
        var content = _osc.ToString();
        _osc.Clear();
        if (content.Length == 0)
        {
            return;
        }

        var separator = content.IndexOf(';');
        if (separator < 0)
        {
            return;
        }

        var code = content[..separator];
        var value = content[(separator + 1)..];
        if (code is "0" or "2")
        {
            var builder = new StringBuilder(Math.Min(value.Length, 200));
            foreach (var ch in value)
            {
                if (builder.Length >= 200)
                {
                    break;
                }

                if (!char.IsControl(ch))
                {
                    builder.Append(ch);
                }
            }

            _buffer.SetTitle(builder.ToString());
        }
    }
}
