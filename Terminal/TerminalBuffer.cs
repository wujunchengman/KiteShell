using System;
using System.Collections.Generic;
using System.Text;

namespace KiteShell.Terminal;

/// <summary>
/// 终端屏幕缓冲区：主/备用屏、滚动历史、光标、滚动区域、字符属性与文本提取。
/// </summary>
public sealed class TerminalBuffer
{
    private static readonly TerminalCell[] EmptyLine = Array.Empty<TerminalCell>();

    private TerminalCell[][] _primary;
    private TerminalCell[][] _alt;
    private readonly List<TerminalCell[]> _scrollback = new();

    private bool _altActive;
    private int _savedPrimaryX;
    private int _savedPrimaryY;
    private int _savedAltX;
    private int _savedAltY;
    private TermColor _savedPrimaryFg;
    private TermColor _savedPrimaryBg;
    private CellFlags _savedPrimaryFlags;
    private TermColor _savedAltFg;
    private TermColor _savedAltBg;
    private CellFlags _savedAltFlags;
    private bool _hasSavedPrimary;
    private bool _hasSavedAlt;

    private int _scrollTop;
    private int _scrollBottom;
    private int _lastCodepoint;
    private int _lastWidth = 1;

    public TerminalBuffer(int columns, int rows)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(2, rows);
        _primary = CreateScreen(Columns, Rows);
        _alt = CreateScreen(Columns, Rows);
        _scrollBottom = Rows - 1;
        ResetPen();
    }

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int ScrollbackLimit { get; set; } = 5000;

    public int CursorX { get; private set; }

    public int CursorY { get; private set; }

    public int ScrollTop => _scrollTop;

    public int ScrollBottom => _scrollBottom;

    public bool IsAltScreen => _altActive;

    public int ScrollbackCount => _scrollback.Count;

    public int MaxScrollOffset => _scrollback.Count;

    public int ScrollOffset { get; set; }

    public int TotalLines => _scrollback.Count + Rows;

    public int ViewStartLine =>
        Math.Max(0, TotalLines - Rows - Math.Clamp(ScrollOffset, 0, MaxScrollOffset));

    public int CursorAbsoluteLine => _scrollback.Count + CursorY;

    public TermColor PenFg;

    public TermColor PenBg;

    public CellFlags PenFlags;

    public bool AutoWrap { get; set; } = true;

    public bool OriginMode { get; set; }

    public bool InsertMode { get; set; }

    public bool CursorVisible { get; set; } = true;

    public bool BracketedPaste { get; set; }

    public bool ApplicationCursorKeys { get; set; }

    public bool ApplicationKeypad { get; set; }

    public bool NewLineMode { get; set; }

    public bool PendingWrap { get; private set; }

    public string? Title { get; private set; }

    public event Action? Changed;

    public event Action<string>? ResponseRequested;

    public event Action<string?>? TitleChanged;

    public event Action? BellRequested;

    public void NotifyChanged() => Changed?.Invoke();

    public void ClearPendingWrap() => PendingWrap = false;

    public void ResetPen()
    {
        PenFg = TermColor.Default;
        PenBg = TermColor.Default;
        PenFlags = CellFlags.None;
    }

    public void SetTitle(string? title)
    {
        Title = title;
        TitleChanged?.Invoke(title);
    }

    public void RingBell() => BellRequested?.Invoke();

    public void RequestResponse(string response) => ResponseRequested?.Invoke(response);

    public void FullReset()
    {
        _primary = CreateScreen(Columns, Rows);
        _alt = CreateScreen(Columns, Rows);
        _scrollback.Clear();
        _altActive = false;
        CursorX = 0;
        CursorY = 0;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _hasSavedPrimary = false;
        _hasSavedAlt = false;
        ScrollOffset = 0;
        PendingWrap = false;
        AutoWrap = true;
        OriginMode = false;
        InsertMode = false;
        CursorVisible = true;
        BracketedPaste = false;
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        NewLineMode = false;
        ResetPen();
        SetTitle(null);
        NotifyChanged();
    }

    public void ClearScrollback()
    {
        _scrollback.Clear();
        ScrollOffset = 0;
        NotifyChanged();
    }

    public void WriteChar(int codepoint, int width)
    {
        if (width <= 0)
        {
            return;
        }

        if (PendingWrap)
        {
            CursorX = 0;
            LineFeed();
            PendingWrap = false;
        }

        if (width == 2 && CursorX >= Columns - 1)
        {
            if (!AutoWrap)
            {
                return;
            }

            CursorX = 0;
            LineFeed();
        }

        if (InsertMode)
        {
            InsertChars(width);
        }

        var row = Screen[CursorY];
        ClearWideAt(row, CursorX);
        if (width == 2 && CursorX + 1 < Columns)
        {
            ClearWideAt(row, CursorX + 1);
        }

        row[CursorX] = new TerminalCell
        {
            Codepoint = codepoint,
            Fg = PenFg,
            Bg = PenBg,
            Flags = PenFlags,
        };

        if (width == 2 && CursorX + 1 < Columns)
        {
            row[CursorX + 1] = new TerminalCell
            {
                Codepoint = 0,
                Continuation = true,
                Fg = PenFg,
                Bg = PenBg,
                Flags = PenFlags,
            };
        }

        _lastCodepoint = codepoint;
        _lastWidth = width;
        CursorX += width;
        if (CursorX >= Columns)
        {
            CursorX = Columns - 1;
            PendingWrap = AutoWrap;
        }
    }

    public void RepeatLastChar(int count)
    {
        if (count <= 0 || _lastCodepoint == 0)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            WriteChar(_lastCodepoint, _lastWidth);
        }
    }

    public void CarriageReturn()
    {
        CursorX = 0;
        PendingWrap = false;
    }

    public void LineFeed()
    {
        PendingWrap = false;
        if (NewLineMode)
        {
            CursorX = 0;
        }

        if (CursorY == _scrollBottom)
        {
            ScrollUp(1);
        }
        else if (CursorY < Rows - 1)
        {
            CursorY++;
        }
    }

    public void ReverseIndex()
    {
        PendingWrap = false;
        if (CursorY == _scrollTop)
        {
            ScrollDown(1);
        }
        else if (CursorY > 0)
        {
            CursorY--;
        }
    }

    public void Backspace()
    {
        PendingWrap = false;
        if (CursorX > 0)
        {
            CursorX--;
        }
    }

    public void Tab(int count = 1)
    {
        PendingWrap = false;
        for (var i = 0; i < count; i++)
        {
            var next = ((CursorX / 8) + 1) * 8;
            CursorX = Math.Min(next, Columns - 1);
        }
    }

    public void BackTab(int count = 1)
    {
        PendingWrap = false;
        for (var i = 0; i < count; i++)
        {
            if (CursorX == 0)
            {
                break;
            }

            CursorX = ((CursorX - 1) / 8) * 8;
        }
    }

    public void MoveTo(int row, int column)
    {
        var rowOffset = OriginMode ? _scrollTop : 0;
        var minRow = OriginMode ? _scrollTop : 0;
        var maxRow = OriginMode ? _scrollBottom : Rows - 1;
        CursorY = Math.Clamp(row + rowOffset, minRow, maxRow);
        CursorX = Math.Clamp(column, 0, Columns - 1);
        PendingWrap = false;
    }

    public void MoveToRow(int row)
    {
        MoveTo(row, CursorX);
    }

    public void MoveToColumn(int column)
    {
        CursorX = Math.Clamp(column, 0, Columns - 1);
        PendingWrap = false;
    }

    public void MoveBy(int columns, int rows)
    {
        var minRow = OriginMode ? _scrollTop : 0;
        var maxRow = OriginMode ? _scrollBottom : Rows - 1;
        CursorX = Math.Clamp(CursorX + columns, 0, Columns - 1);
        CursorY = Math.Clamp(CursorY + rows, minRow, maxRow);
        PendingWrap = false;
    }

    public void SaveCursor()
    {
        if (_altActive)
        {
            _savedAltX = CursorX;
            _savedAltY = CursorY;
            _savedAltFg = PenFg;
            _savedAltBg = PenBg;
            _savedAltFlags = PenFlags;
            _hasSavedAlt = true;
        }
        else
        {
            _savedPrimaryX = CursorX;
            _savedPrimaryY = CursorY;
            _savedPrimaryFg = PenFg;
            _savedPrimaryBg = PenBg;
            _savedPrimaryFlags = PenFlags;
            _hasSavedPrimary = true;
        }

        PendingWrap = false;
    }

    public void RestoreCursor()
    {
        if (_altActive && _hasSavedAlt)
        {
            CursorX = Math.Clamp(_savedAltX, 0, Columns - 1);
            CursorY = Math.Clamp(_savedAltY, 0, Rows - 1);
            PenFg = _savedAltFg;
            PenBg = _savedAltBg;
            PenFlags = _savedAltFlags;
        }
        else if (!_altActive && _hasSavedPrimary)
        {
            CursorX = Math.Clamp(_savedPrimaryX, 0, Columns - 1);
            CursorY = Math.Clamp(_savedPrimaryY, 0, Rows - 1);
            PenFg = _savedPrimaryFg;
            PenBg = _savedPrimaryBg;
            PenFlags = _savedPrimaryFlags;
        }

        PendingWrap = false;
    }

    public void EnterAltScreen(bool clear, bool saveCursor)
    {
        if (_altActive)
        {
            return;
        }

        if (saveCursor)
        {
            SaveCursor();
        }

        _altActive = true;
        CursorX = 0;
        CursorY = 0;
        PendingWrap = false;
        ScrollOffset = 0;
        if (clear)
        {
            ClearAllRows(_alt);
        }
    }

    public void ExitAltScreen(bool restoreCursor)
    {
        if (!_altActive)
        {
            return;
        }

        _altActive = false;
        PendingWrap = false;
        ScrollOffset = 0;
        CursorX = 0;
        CursorY = 0;
        if (restoreCursor && _hasSavedPrimary)
        {
            CursorX = Math.Clamp(_savedPrimaryX, 0, Columns - 1);
            CursorY = Math.Clamp(_savedPrimaryY, 0, Rows - 1);
        }
    }

    public void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top - 1, 0, Rows - 1);
        bottom = Math.Clamp(bottom - 1, 0, Rows - 1);
        if (bottom <= top)
        {
            _scrollTop = 0;
            _scrollBottom = Rows - 1;
        }
        else
        {
            _scrollTop = top;
            _scrollBottom = bottom;
        }

        PendingWrap = false;
        MoveTo(0, 0);
    }

    public void EraseInDisplay(int mode)
    {
        var screen = Screen;
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var y = CursorY + 1; y < Rows; y++)
                {
                    ClearRow(screen, y);
                }

                break;
            case 1:
                for (var y = 0; y < CursorY; y++)
                {
                    ClearRow(screen, y);
                }

                EraseInLine(1);
                break;
            case 2:
                for (var y = 0; y < Rows; y++)
                {
                    ClearRow(screen, y);
                }

                break;
            case 3:
                _scrollback.Clear();
                ScrollOffset = 0;
                break;
        }

        PendingWrap = false;
    }

    public void EraseInLine(int mode)
    {
        var row = Screen[CursorY];
        var start = mode == 1 ? 0 : CursorX;
        var end = mode == 1 ? CursorX + 1 : Columns;
        for (var x = start; x < end; x++)
        {
            row[x] = TerminalCell.Blank(PenBg);
        }

        PendingWrap = false;
    }

    public void EraseChars(int count)
    {
        var row = Screen[CursorY];
        var end = Math.Min(Columns, CursorX + Math.Max(0, count));
        for (var x = CursorX; x < end; x++)
        {
            row[x] = TerminalCell.Blank(PenBg);
        }
    }

    public void InsertChars(int count)
    {
        if (count <= 0)
        {
            return;
        }

        var row = Screen[CursorY];
        count = Math.Min(count, Columns - CursorX);
        for (var x = Columns - 1; x >= CursorX + count; x--)
        {
            row[x] = row[x - count];
        }

        for (var x = CursorX; x < CursorX + count; x++)
        {
            row[x] = TerminalCell.Blank(PenBg);
        }

        PendingWrap = false;
    }

    public void DeleteChars(int count)
    {
        if (count <= 0)
        {
            return;
        }

        var row = Screen[CursorY];
        count = Math.Min(count, Columns - CursorX);
        for (var x = CursorX; x < Columns - count; x++)
        {
            row[x] = row[x + count];
        }

        for (var x = Columns - count; x < Columns; x++)
        {
            row[x] = TerminalCell.Blank(PenBg);
        }

        PendingWrap = false;
    }

    public void InsertLines(int count)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom)
        {
            return;
        }

        var screen = Screen;
        count = Math.Min(count, _scrollBottom - CursorY + 1);
        for (var y = _scrollBottom; y >= CursorY + count; y--)
        {
            screen[y] = screen[y - count];
        }

        for (var y = CursorY; y < CursorY + count; y++)
        {
            screen[y] = CreateBlankRow();
        }

        PendingWrap = false;
    }

    public void DeleteLines(int count)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom)
        {
            return;
        }

        var screen = Screen;
        count = Math.Min(count, _scrollBottom - CursorY + 1);
        for (var y = CursorY; y <= _scrollBottom - count; y++)
        {
            screen[y] = screen[y + count];
        }

        for (var y = _scrollBottom - count + 1; y <= _scrollBottom; y++)
        {
            screen[y] = CreateBlankRow();
        }

        PendingWrap = false;
    }

    public void ScrollUp(int count)
    {
        var screen = Screen;
        count = Math.Max(0, count);
        for (var i = 0; i < count; i++)
        {
            if (!_altActive && _scrollTop == 0)
            {
                _scrollback.Add(screen[0]);
                TrimScrollback();
            }

            for (var y = _scrollTop; y < _scrollBottom; y++)
            {
                screen[y] = screen[y + 1];
            }

            screen[_scrollBottom] = CreateBlankRow();
        }

        if (!_altActive && _scrollTop == 0)
        {
            ScrollOffset = 0;
        }
    }

    public void ScrollDown(int count)
    {
        var screen = Screen;
        count = Math.Max(0, count);
        for (var i = 0; i < count; i++)
        {
            for (var y = _scrollBottom; y > _scrollTop; y--)
            {
                screen[y] = screen[y - 1];
            }

            screen[_scrollTop] = CreateBlankRow();
        }
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(2, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        _primary = ResizeScreen(_primary, columns, rows);
        _alt = ResizeScreen(_alt, columns, rows);
        Columns = columns;
        Rows = rows;
        CursorX = Math.Clamp(CursorX, 0, Columns - 1);
        CursorY = Math.Clamp(CursorY, 0, Rows - 1);
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        PendingWrap = false;
        ScrollOffset = Math.Clamp(ScrollOffset, 0, MaxScrollOffset);

        // 尺寸变化后重绘整个终端。
        Changed?.Invoke();
    }

    public TerminalCell[] GetLine(int absoluteLine)
    {
        if (absoluteLine < 0)
        {
            return EmptyLine;
        }

        if (absoluteLine < _scrollback.Count)
        {
            return _scrollback[absoluteLine];
        }

        var index = absoluteLine - _scrollback.Count;
        return index >= 0 && index < Rows ? Screen[index] : EmptyLine;
    }

    public string GetLineText(int absoluteLine, int startColumn, int endColumnExclusive)
    {
        var row = GetLine(absoluteLine);
        if (row.Length == 0)
        {
            return string.Empty;
        }

        startColumn = Math.Clamp(startColumn, 0, Columns);
        endColumnExclusive = Math.Clamp(endColumnExclusive, 0, Columns);
        var builder = new StringBuilder();
        for (var x = startColumn; x < endColumnExclusive; x++)
        {
            var cell = row[x];
            if (cell.Continuation)
            {
                continue;
            }

            if (cell.Codepoint == 0)
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(char.ConvertFromUtf32(cell.Codepoint));
            }
        }

        return builder.ToString().TrimEnd();
    }

    public string GetText(int startLine, int startColumn, int endLine, int endColumn)
    {
        if (startLine > endLine || (startLine == endLine && startColumn > endColumn))
        {
            (startLine, endLine) = (endLine, startLine);
            (startColumn, endColumn) = (endColumn, startColumn);
        }

        if (startLine == endLine && startColumn == endColumn)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var line = startLine; line <= endLine && line < TotalLines; line++)
        {
            var from = line == startLine ? startColumn : 0;
            var to = line == endLine ? endColumn : Columns;
            builder.Append(GetLineText(line, from, to));
            if (line != endLine)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private TerminalCell[][] Screen => _altActive ? _alt : _primary;

    private void TrimScrollback()
    {
        while (_scrollback.Count > ScrollbackLimit)
        {
            _scrollback.RemoveAt(0);
        }
    }

    private void ClearWideAt(TerminalCell[] row, int index)
    {
        if (index < 0 || index >= Columns)
        {
            return;
        }

        var cell = row[index];
        if (cell.Continuation && index > 0)
        {
            row[index - 1] = TerminalCell.Blank(PenBg);
            row[index] = TerminalCell.Blank(PenBg);
            return;
        }

        if (cell.Codepoint != 0
            && UnicodeWidth.GetWidth(cell.Codepoint) == 2
            && index + 1 < Columns
            && row[index + 1].Continuation)
        {
            row[index] = TerminalCell.Blank(PenBg);
            row[index + 1] = TerminalCell.Blank(PenBg);
        }
    }

    private void ClearRow(TerminalCell[][] screen, int rowIndex)
    {
        screen[rowIndex] = CreateBlankRow();
    }

    private void ClearAllRows(TerminalCell[][] screen)
    {
        for (var y = 0; y < screen.Length; y++)
        {
            screen[y] = CreateBlankRow();
        }
    }

    private TerminalCell[] CreateBlankRow()
    {
        var row = new TerminalCell[Columns];
        for (var x = 0; x < row.Length; x++)
        {
            row[x] = TerminalCell.Blank(PenBg);
        }

        return row;
    }

    private static TerminalCell[][] CreateScreen(int columns, int rows)
    {
        var screen = new TerminalCell[rows][];
        for (var y = 0; y < rows; y++)
        {
            screen[y] = new TerminalCell[columns];
        }

        return screen;
    }

    private static TerminalCell[][] ResizeScreen(TerminalCell[][] source, int columns, int rows)
    {
        var target = CreateScreen(columns, rows);
        var copyRows = Math.Min(rows, source.Length);
        for (var y = 0; y < copyRows; y++)
        {
            var copyColumns = Math.Min(columns, source[y].Length);
            Array.Copy(source[y], target[y], copyColumns);
        }

        return target;
    }
}
