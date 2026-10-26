using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Threading;

namespace KiteShell.Terminal;

/// <summary>
/// 自绘终端控件：负责 VT 缓冲渲染、选区、滚动条、键盘/剪贴板输入。
/// </summary>
public sealed class TerminalControl : Control
{
    private const double PadX = 8;
    private const double PadY = 6;
    private const double ScrollbarWidth = 10;

    private readonly TerminalBuffer _buffer = new(80, 24);
    private readonly AnsiParser _parser;
    private readonly DispatcherTimer _blinkTimer;
    private readonly Dictionary<uint, IBrush> _brushes = new();

    private double _fontSize = 14;
    private string _fontFamilyName =
        "JetBrains Mono, Cascadia Mono, Consolas, DejaVu Sans Mono, Noto Sans Mono CJK SC, monospace";
    private FontFamily _fontFamily;
    private Typeface _regular;
    private Typeface _bold;
    private Typeface _italic;
    private Typeface _boldItalic;
    private double _cellWidth;
    private double _cellHeight;
    private double _baseline;
    private bool _metricsDirty = true;
    private bool _cursorPhase;

    private bool _hasSelection;
    private bool _selecting;
    private int _selectionAnchorLine;
    private int _selectionAnchorColumn;
    private int _selectionEndLine;
    private int _selectionEndColumn;

    private bool _draggingScrollbar;
    private double _scrollDragOffset;
    private double _thumbTop;
    private double _thumbHeight;
    private double _trackTop;
    private double _trackHeight;
    private bool _pointerOverScrollbar;
    private TerminalImeClient? _imeClient;
    private string? _preeditText;
    private Rect _imeCursorRectangle;
    private bool _hasImeCursorRectangle;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        _fontFamily = new FontFamily(_fontFamilyName);
        BuildContextMenu();
        AddHandler(TextInputMethodClientRequestedEvent, OnInputMethodClientRequested);

        _parser = new AnsiParser(_buffer);
        _buffer.Changed += () =>
        {
            InvalidateVisual();
            SyncImeCursorRectangle();
        };
        _buffer.TitleChanged += title => TitleChanged?.Invoke(title);
        _buffer.ResponseRequested += response => UserInput?.Invoke(response);
        _buffer.BellRequested += () => Bell?.Invoke();

        _blinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(530),
        };
        _blinkTimer.Tick += (_, _) =>
        {
            _cursorPhase = !_cursorPhase;
            if (_buffer.CursorVisible)
            {
                InvalidateVisual();
            }
        };
    }

    public TerminalBuffer Buffer => _buffer;

    public string? SessionTitle => _buffer.Title;

    public double FontSize
    {
        get => _fontSize;
        set
        {
            var clamped = Math.Clamp(value, 8, 40);
            if (Math.Abs(clamped - _fontSize) < 0.1)
            {
                return;
            }

            _fontSize = clamped;
            _metricsDirty = true;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public string FontFamilyName
    {
        get => _fontFamilyName;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _fontFamilyName)
            {
                return;
            }

            _fontFamilyName = value;
            _fontFamily = new FontFamily(value);
            _metricsDirty = true;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public bool HasSelection => _hasSelection;

    public event Action<string>? UserInput;

    public event Action<int, int>? Resized;

    public event Action<string?>? TitleChanged;

    public event Action? Bell;

    public void Feed(string text)
    {
        _cursorPhase = true;
        _parser.Feed(text);
    }

    public void ResetTerminal()
    {
        _buffer.FullReset();
        _parser.Reset();
        _hasSelection = false;
        _selecting = false;
        _buffer.ScrollOffset = 0;
        InvalidateVisual();
    }

    public void AppendLocalLine(string text)
    {
        Feed(text + "\r\n");
    }

    public void ScrollToBottom()
    {
        _buffer.ScrollOffset = 0;
        InvalidateVisual();
        SyncImeCursorRectangle();
    }

    public void SendTextToSession(string text, bool appendEnter = true)
    {
        _buffer.ScrollOffset = 0;
        _buffer.ClearPendingWrap();
        UserInput?.Invoke(appendEnter ? text + "\r" : text);
        Focus();
        InvalidateVisual();
        SyncImeCursorRectangle();
    }

    public Task CopySelectionAsync() => CopySelectionCoreAsync();

    public Task PasteAsync() => PasteCoreAsync();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _blinkTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _blinkTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _preeditText = null;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        UpdateFontMetrics();
        var width = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 480 : availableSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateFontMetrics();
        var columns = (int)Math.Floor((finalSize.Width - (PadX * 2) - ScrollbarWidth) / Math.Max(1, _cellWidth));
        var rows = (int)Math.Floor((finalSize.Height - (PadY * 2)) / Math.Max(1, _cellHeight));
        columns = Math.Max(20, columns);
        rows = Math.Max(4, rows);

        if (columns != _buffer.Columns || rows != _buffer.Rows)
        {
            _buffer.Resize(columns, rows);
            Resized?.Invoke(columns, rows);
        }

        SyncImeCursorRectangle();
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_cellWidth <= 0 || _cellHeight <= 0)
        {
            return;
        }

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        context.FillRectangle(
            GetBrush(TerminalPalette.DefaultBackground | 0xFF000000u),
            new Rect(0, 0, width, height));

        var columns = Math.Min(_buffer.Columns, Math.Max(0, (int)((width - (PadX * 2)) / _cellWidth)));
        var rows = Math.Min(_buffer.Rows, Math.Max(0, (int)((height - (PadY * 2)) / _cellHeight)));
        var viewStart = _buffer.ViewStartLine;
        var totalLines = _buffer.TotalLines;

        for (var row = 0; row < rows; row++)
        {
            var lineIndex = viewStart + row;
            if (lineIndex >= totalLines)
            {
                break;
            }

            var line = _buffer.GetLine(lineIndex);
            var cellCount = Math.Min(columns, line.Length);
            if (cellCount == 0)
            {
                continue;
            }

            var y = PadY + (row * _cellHeight);

            // 背景色按连续区间绘制。
            var runStart = 0;
            var background = EffectiveBackground(line[0]);
            for (var x = 1; x <= cellCount; x++)
            {
                var current = x < cellCount ? EffectiveBackground(line[x]) : uint.MaxValue;
                if (current == background)
                {
                    continue;
                }

                if (background != TerminalPalette.DefaultBackground)
                {
                    context.FillRectangle(
                        GetBrush(background),
                        new Rect(
                            PadX + (runStart * _cellWidth),
                            y,
                            (x - runStart) * _cellWidth,
                            _cellHeight));
                }

                runStart = x;
                background = current;
            }

            if (_hasSelection)
            {
                var (selectionStart, selectionEnd) = GetSelectionRangeForLine(lineIndex);
                if (selectionEnd > selectionStart)
                {
                    context.FillRectangle(
                        GetBrush(TerminalPalette.Selection),
                        new Rect(
                            PadX + (selectionStart * _cellWidth),
                            y,
                            (selectionEnd - selectionStart) * _cellWidth,
                            _cellHeight));
                }
            }

            // 文本按样式分组成 run 绘制。
            var column = 0;
            while (column < cellCount)
            {
                if (line[column].Continuation)
                {
                    column++;
                    continue;
                }

                var start = column;
                var flags = line[column].Flags & (CellFlags.Bold | CellFlags.Italic | CellFlags.Underline | CellFlags.Strike);
                var foreground = EffectiveForeground(line[column], _cursorPhase);
                var builder = new StringBuilder();

                while (column < cellCount)
                {
                    var cell = line[column];
                    if (cell.Continuation)
                    {
                        column++;
                        continue;
                    }

                    if ((cell.Flags & (CellFlags.Bold | CellFlags.Italic | CellFlags.Underline | CellFlags.Strike)) != flags)
                    {
                        break;
                    }

                    if (EffectiveForeground(cell, _cursorPhase) != foreground)
                    {
                        break;
                    }

                    if (cell.Codepoint == 0)
                    {
                        builder.Append(' ');
                    }
                    else
                    {
                        builder.Append(char.ConvertFromUtf32(cell.Codepoint));
                    }

                    column++;
                }

                if (builder.Length == 0 || (flags & CellFlags.Hidden) != 0)
                {
                    continue;
                }

                var typeface = GetTypeface(flags);
                var text = new FormattedText(
                    builder.ToString(),
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    _fontSize,
                    GetBrush(foreground));

                if ((flags & CellFlags.Underline) != 0)
                {
                    text.SetTextDecorations(TextDecorations.Underline);
                }
                else if ((flags & CellFlags.Strike) != 0)
                {
                    text.SetTextDecorations(TextDecorations.Strikethrough);
                }

                context.DrawText(
                    text,
                    new Point(
                        PadX + (start * _cellWidth),
                        y + _baseline - text.Baseline));
            }
        }

        DrawCursor(context, width, height, viewStart, rows);
        DrawPreedit(context);
        DrawScrollbar(context, width, height);

        if (_buffer.ScrollOffset > 0)
        {
            DrawScrollbackIndicator(context, width);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed)
        {
            _ = PasteCoreAsync();
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            if (IsInScrollbar(point.Position) && _buffer.MaxScrollOffset > 0)
            {
                BeginScrollbarDrag(point.Position);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            var (column, line) = PointToCell(point.Position);
            _selecting = true;
            _hasSelection = false;
            _selectionAnchorColumn = column;
            _selectionAnchorLine = line;
            _selectionEndColumn = column;
            _selectionEndLine = line;
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var overScrollbar = IsInScrollbar(position) && _buffer.MaxScrollOffset > 0;
        if (overScrollbar != _pointerOverScrollbar)
        {
            _pointerOverScrollbar = overScrollbar;
            InvalidateVisual();
        }

        if (_draggingScrollbar)
        {
            UpdateScrollbarDrag(position);
            e.Handled = true;
            return;
        }

        if (!_selecting)
        {
            return;
        }

        var (column, line) = PointToCell(position);
        if (column == _selectionEndColumn && line == _selectionEndLine)
        {
            return;
        }

        _selectionEndColumn = column;
        _selectionEndLine = line;
        _hasSelection = _selectionEndColumn != _selectionAnchorColumn || _selectionEndLine != _selectionAnchorLine;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
        }

        if (_draggingScrollbar)
        {
            _draggingScrollbar = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _selecting = false;
        _draggingScrollbar = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            FontSize += e.Delta.Y > 0 ? 1 : -1;
            e.Handled = true;
            return;
        }

        if (_buffer.IsAltScreen)
        {
            var sequence = e.Delta.Y > 0 ? "\u001B[A" : "\u001B[B";
            var text = sequence + sequence + sequence;
            UserInput?.Invoke(text);
        }
        else
        {
            _buffer.ScrollOffset = Math.Clamp(
                _buffer.ScrollOffset + (e.Delta.Y > 0 ? 3 : -3),
                0,
                _buffer.MaxScrollOffset);
            InvalidateVisual();
            SyncImeCursorRectangle();
        }

        e.Handled = true;
    }

    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        SelectWordAt(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (control && shift)
        {
            switch (e.Key)
            {
                case Key.C:
                case Key.Insert:
                    _ = CopySelectionCoreAsync();
                    e.Handled = true;
                    return;
                case Key.V:
                    _ = PasteCoreAsync();
                    e.Handled = true;
                    return;
                case Key.OemPlus:
                case Key.Add:
                    FontSize += 1;
                    e.Handled = true;
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    FontSize -= 1;
                    e.Handled = true;
                    return;
                case Key.D0:
                case Key.NumPad0:
                    FontSize = 14;
                    e.Handled = true;
                    return;
                case Key.Home:
                    _buffer.ScrollOffset = _buffer.MaxScrollOffset;
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                case Key.End:
                    _buffer.ScrollOffset = 0;
                    InvalidateVisual();
                    e.Handled = true;
                    return;
            }
        }

        if (!control && shift)
        {
            switch (e.Key)
            {
                case Key.Insert:
                    _ = PasteCoreAsync();
                    e.Handled = true;
                    return;
                case Key.PageUp:
                    ScrollByPage(1);
                    e.Handled = true;
                    return;
                case Key.PageDown:
                    ScrollByPage(-1);
                    e.Handled = true;
                    return;
            }
        }

        if (control && e.Key == Key.Tab)
        {
            // 交给外层 TabControl 切换标签。
            return;
        }

        var sequence = TerminalKeyEncoder.Encode(e.Key, e.KeyModifiers, _buffer.ApplicationCursorKeys);
        if (sequence is not null)
        {
            SendInput(sequence);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        _preeditText = null;
        SendInput(e.Text);
        e.Handled = true;
    }

    private void SendInput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _buffer.ScrollOffset = 0;
        _hasSelection = false;
        _cursorPhase = true;
        UserInput?.Invoke(text);
        InvalidateVisual();
        SyncImeCursorRectangle();
    }

    private void ScrollByPage(int direction)
    {
        _buffer.ScrollOffset = Math.Clamp(
            _buffer.ScrollOffset + (direction * _buffer.Rows),
            0,
            _buffer.MaxScrollOffset);
        InvalidateVisual();
        SyncImeCursorRectangle();
    }

    private async Task CopySelectionCoreAsync()
    {
        if (!_hasSelection)
        {
            return;
        }

        GetNormalizedSelection(out var startLine, out var startColumn, out var endLine, out var endColumn);
        var text = _buffer.GetText(startLine, startColumn, endLine, endColumn + 1);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private async Task PasteCoreAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null)
        {
            return;
        }

        var text = await topLevel.Clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r", StringComparison.Ordinal);

        if (_buffer.BracketedPaste)
        {
            text = "\u001B[200~" + text + "\u001B[201~";
        }

        SendInput(text);
    }

    private void SelectWordAt(Point position)
    {
        var (column, line) = PointToCell(position);
        var row = _buffer.GetLine(line);
        if (row.Length == 0 || column >= row.Length)
        {
            return;
        }

        var index = column;
        if (row[index].Continuation && index > 0)
        {
            index--;
        }

        if (!IsWordCell(row[index]))
        {
            return;
        }

        var start = index;
        while (start > 0 && IsWordCell(row[start - 1]))
        {
            start--;
        }

        var end = index;
        while (end + 1 < row.Length && IsWordCell(row[end + 1]))
        {
            end++;
        }

        _selectionAnchorLine = line;
        _selectionAnchorColumn = start;
        _selectionEndLine = line;
        _selectionEndColumn = end;
        _hasSelection = true;
        InvalidateVisual();
    }

    private static bool IsWordCell(TerminalCell cell)
    {
        if (cell.Continuation || cell.Codepoint == 0)
        {
            return false;
        }

        if (cell.Codepoint < 128)
        {
            var ch = (char)cell.Codepoint;
            return char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/' or ':' or '@' or '~';
        }

        return true;
    }

    private (int Column, int Line) PointToCell(Point position)
    {
        var column = (int)Math.Floor((position.X - PadX) / Math.Max(1, _cellWidth));
        var row = (int)Math.Floor((position.Y - PadY) / Math.Max(1, _cellHeight));
        column = Math.Clamp(column, 0, _buffer.Columns - 1);
        row = Math.Clamp(row, 0, Math.Min(_buffer.Rows, Math.Max(1, (int)((Bounds.Height - (PadY * 2)) / _cellHeight))) - 1);
        var line = _buffer.ViewStartLine + row;
        line = Math.Clamp(line, 0, _buffer.TotalLines - 1);
        return (column, line);
    }

    private (int Start, int End) GetSelectionRangeForLine(int lineIndex)
    {
        if (!_hasSelection)
        {
            return (0, 0);
        }

        GetNormalizedSelection(out var startLine, out var startColumn, out var endLine, out var endColumn);
        if (lineIndex < startLine || lineIndex > endLine)
        {
            return (0, 0);
        }

        var start = lineIndex == startLine ? startColumn : 0;
        var end = lineIndex == endLine ? endColumn + 1 : _buffer.Columns;
        return (start, Math.Min(end, _buffer.Columns));
    }

    private void GetNormalizedSelection(
        out int startLine,
        out int startColumn,
        out int endLine,
        out int endColumn)
    {
        startLine = _selectionAnchorLine;
        startColumn = _selectionAnchorColumn;
        endLine = _selectionEndLine;
        endColumn = _selectionEndColumn;

        if (startLine > endLine || (startLine == endLine && startColumn > endColumn))
        {
            (startLine, endLine) = (endLine, startLine);
            (startColumn, endColumn) = (endColumn, startColumn);
        }
    }

    private void DrawCursor(DrawingContext context, double width, double height, int viewStart, int rows)
    {
        if (!_buffer.CursorVisible || _buffer.ScrollOffset != 0)
        {
            return;
        }

        var cursorLine = _buffer.CursorAbsoluteLine;
        if (cursorLine < viewStart || cursorLine >= viewStart + rows)
        {
            return;
        }

        var x = PadX + (_buffer.CursorX * _cellWidth);
        var y = PadY + ((cursorLine - viewStart) * _cellHeight);
        if (x + _cellWidth > width || y + _cellHeight > height)
        {
            return;
        }

        var focused = IsFocused;
        if (focused && !_cursorPhase)
        {
            return;
        }

        var color = focused ? 0xA07AA2F7u : 0x507AA2F7u;
        context.FillRectangle(GetBrush(color), new Rect(x, y + 1, _cellWidth, _cellHeight - 2));
    }

    private void DrawScrollbar(DrawingContext context, double width, double height)
    {
        if (_buffer.MaxScrollOffset <= 0 || height <= 20)
        {
            return;
        }

        _trackTop = 2;
        _trackHeight = height - 4;
        var totalLines = Math.Max(1, _buffer.TotalLines);
        _thumbHeight = Math.Max(24, _trackHeight * _buffer.Rows / totalLines);
        var maxTop = Math.Max(0, _trackHeight - _thumbHeight);
        var scrollRange = Math.Max(1, totalLines - _buffer.Rows);
        var ratio = (double)_buffer.ViewStartLine / scrollRange;
        _thumbTop = _trackTop + (maxTop * ratio);

        var thumbColor = _pointerOverScrollbar || _draggingScrollbar
            ? TerminalPalette.ScrollbarThumbHover
            : TerminalPalette.ScrollbarThumb;
        var rect = new Rect(width - ScrollbarWidth + 2, _thumbTop, ScrollbarWidth - 4, _thumbHeight);
        context.FillRectangle(GetBrush(thumbColor), rect, 4);
    }

    private void DrawScrollbackIndicator(DrawingContext context, double width)
    {
        var text = new FormattedText(
            $"↑ {_buffer.ScrollOffset}",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _regular,
            10,
            GetBrush(0xCCFFFFFF));
        var rect = new Rect(width - text.Width - 24, Bounds.Height - 22, text.Width + 14, 16);
        context.FillRectangle(GetBrush(0xAA1E2433), rect, 4);
        context.DrawText(text, new Point(rect.X + 7, rect.Y + 2));
    }

    private bool IsInScrollbar(Point position)
    {
        return position.X >= Bounds.Width - ScrollbarWidth;
    }

    private void BeginScrollbarDrag(Point position)
    {
        var inThumb = position.Y >= _thumbTop && position.Y <= _thumbTop + _thumbHeight;
        if (!inThumb)
        {
            var ratio = Math.Clamp((position.Y - _trackTop) / Math.Max(1, _trackHeight), 0, 1);
            _buffer.ScrollOffset = (int)Math.Round((1 - ratio) * _buffer.MaxScrollOffset);
            InvalidateVisual();
            SyncImeCursorRectangle();
            _scrollDragOffset = _thumbHeight / 2;
        }
        else
        {
            _scrollDragOffset = position.Y - _thumbTop;
        }

        _draggingScrollbar = true;
    }

    private void UpdateScrollbarDrag(Point position)
    {
        var maxTop = Math.Max(0, _trackHeight - _thumbHeight);
        var top = Math.Clamp(position.Y - _scrollDragOffset - _trackTop, 0, maxTop);
        var ratio = maxTop <= 0 ? 1 : top / maxTop;
        _buffer.ScrollOffset = (int)Math.Round((1 - ratio) * _buffer.MaxScrollOffset);
        InvalidateVisual();
        SyncImeCursorRectangle();
    }

    private void BuildContextMenu()
    {
        var copy = new MenuItem { Header = "复制" };
        copy.Click += (_, _) => _ = CopySelectionCoreAsync();
        var paste = new MenuItem { Header = "粘贴" };
        paste.Click += (_, _) => _ = PasteCoreAsync();
        var selectAll = new MenuItem { Header = "全选" };
        selectAll.Click += (_, _) => SelectAllText();

        var menu = new ContextMenu();
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(selectAll);
        ContextMenu = menu;
    }

    private void SelectAllText()
    {
        _selectionAnchorLine = 0;
        _selectionAnchorColumn = 0;
        _selectionEndLine = _buffer.TotalLines - 1;
        _selectionEndColumn = _buffer.Columns - 1;
        _hasSelection = true;
        InvalidateVisual();
    }

    private void OnInputMethodClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        _imeClient ??= new TerminalImeClient(this);
        e.Client = _imeClient;

        // 平台只在 CursorRectangleChanged 时更新候选框位置，客户端注册后主动上报一次。
        _hasImeCursorRectangle = false;
        Dispatcher.UIThread.Post(SyncImeCursorRectangle);
    }

    private void SetPreeditText(string? text)
    {
        _preeditText = string.IsNullOrEmpty(text) ? null : text;
        InvalidateVisual();
    }

    private Rect GetCursorRectangle()
    {
        if (_cellWidth <= 0 || _cellHeight <= 0 || _buffer.ScrollOffset != 0)
        {
            return default;
        }

        var row = _buffer.CursorAbsoluteLine - _buffer.ViewStartLine;
        if (row < 0 || row >= _buffer.Rows)
        {
            return default;
        }

        return new Rect(
            PadX + (_buffer.CursorX * _cellWidth),
            PadY + (row * _cellHeight),
            _cellWidth,
            _cellHeight);
    }

    /// <summary>
    /// 光标位置变化时通知输入法更新候选框位置；光标不可见（正在回看历史）时保持原位。
    /// </summary>
    private void SyncImeCursorRectangle()
    {
        var rectangle = GetCursorRectangle();
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return;
        }

        if (_hasImeCursorRectangle && rectangle == _imeCursorRectangle)
        {
            return;
        }

        _imeCursorRectangle = rectangle;
        _hasImeCursorRectangle = true;
        _imeClient?.NotifyCursorRectangleChanged();
    }

    private void DrawPreedit(DrawingContext context)
    {
        if (string.IsNullOrEmpty(_preeditText) || _buffer.ScrollOffset != 0)
        {
            return;
        }

        var cursorRect = GetCursorRectangle();
        if (cursorRect.Width <= 0 || cursorRect.Height <= 0)
        {
            return;
        }

        var text = new FormattedText(
            _preeditText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _regular,
            _fontSize,
            GetBrush(TerminalPalette.DefaultForeground));
        text.SetTextDecorations(TextDecorations.Underline);

        var width = Math.Min(Math.Max(text.Width + 4, cursorRect.Width), Bounds.Width - cursorRect.X);
        context.FillRectangle(
            GetBrush(0xE62A3140),
            new Rect(cursorRect.X, cursorRect.Y, Math.Max(1, width), cursorRect.Height));
        context.DrawText(
            text,
            new Point(cursorRect.X + 1, cursorRect.Y + _baseline - text.Baseline));
    }

    private void UpdateFontMetrics()
    {
        if (!_metricsDirty && _cellWidth > 0)
        {
            return;
        }

        _regular = new Typeface(_fontFamily, FontStyle.Normal, FontWeight.Normal);
        _bold = new Typeface(_fontFamily, FontStyle.Normal, FontWeight.Bold);
        _italic = new Typeface(_fontFamily, FontStyle.Italic, FontWeight.Normal);
        _boldItalic = new Typeface(_fontFamily, FontStyle.Italic, FontWeight.Bold);

        var widthText = new FormattedText(
            "M",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _regular,
            _fontSize,
            Brushes.White);
        var heightText = new FormattedText(
            "Mg",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _regular,
            _fontSize,
            Brushes.White);

        _cellWidth = Math.Max(4, Math.Ceiling(widthText.WidthIncludingTrailingWhitespace));
        _cellHeight = Math.Max(10, Math.Ceiling(heightText.Height) + 1);
        _baseline = heightText.Baseline;
        _metricsDirty = false;
    }

    private Typeface GetTypeface(CellFlags flags)
    {
        var bold = (flags & CellFlags.Bold) != 0;
        var italic = (flags & CellFlags.Italic) != 0;
        if (bold && italic)
        {
            return _boldItalic;
        }

        if (bold)
        {
            return _bold;
        }

        return italic ? _italic : _regular;
    }

    private static uint EffectiveBackground(in TerminalCell cell)
    {
        var background = cell.Bg.IsDefault ? TerminalPalette.DefaultBackground : cell.Bg.Value;
        if ((cell.Flags & CellFlags.Inverse) != 0)
        {
            return cell.Fg.IsDefault ? TerminalPalette.DefaultForeground : cell.Fg.Value;
        }

        return background;
    }

    private static uint EffectiveForeground(in TerminalCell cell, bool blinkPhase)
    {
        var foreground = cell.Fg.IsDefault ? TerminalPalette.DefaultForeground : cell.Fg.Value;
        var background = cell.Bg.IsDefault ? TerminalPalette.DefaultBackground : cell.Bg.Value;

        if ((cell.Flags & CellFlags.Inverse) != 0)
        {
            (foreground, background) = (background, foreground);
        }

        if ((cell.Flags & CellFlags.Hidden) != 0)
        {
            return background;
        }

        if ((cell.Flags & CellFlags.Blink) != 0 && !blinkPhase)
        {
            return background;
        }

        if ((cell.Flags & CellFlags.Dim) != 0)
        {
            return Blend(foreground, background, 0.55);
        }

        return foreground;
    }

    private static uint Blend(uint foreground, uint background, double amount)
    {
        var r = (byte)((((foreground >> 16) & 0xFF) * amount) + (((background >> 16) & 0xFF) * (1 - amount)));
        var g = (byte)((((foreground >> 8) & 0xFF) * amount) + (((background >> 8) & 0xFF) * (1 - amount)));
        var b = (byte)(((foreground & 0xFF) * amount) + ((background & 0xFF) * (1 - amount)));
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    private sealed class TerminalImeClient : TextInputMethodClient
    {
        private readonly TerminalControl _owner;

        public TerminalImeClient(TerminalControl owner)
        {
            _owner = owner;
        }

        public override Visual TextViewVisual => _owner;

        public override bool SupportsPreedit => true;

        public override bool SupportsSurroundingText => false;

        public override string SurroundingText => string.Empty;

        public override Rect CursorRectangle => _owner.GetCursorRectangle();

        public override TextSelection Selection { get; set; }

        public override void SetPreeditText(string? preeditText) => _owner.SetPreeditText(preeditText);

        public override void SetPreeditText(string? preeditText, int? cursorPosition) =>
            _owner.SetPreeditText(preeditText);

        public void NotifyCursorRectangleChanged() => RaiseCursorRectangleChanged();

        public override void ExecuteContextMenuAction(ContextMenuAction action)
        {
            switch (action)
            {
                case ContextMenuAction.Copy:
                    _ = _owner.CopySelectionCoreAsync();
                    break;
                case ContextMenuAction.Paste:
                    _ = _owner.PasteCoreAsync();
                    break;
                case ContextMenuAction.SelectAll:
                    _owner.SelectAllText();
                    break;
            }
        }

    }

    private IBrush GetBrush(uint argb)
    {
        if (_brushes.TryGetValue(argb, out var brush))
        {
            return brush;
        }

        brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24),
            (byte)(argb >> 16),
            (byte)(argb >> 8),
            (byte)argb));
        _brushes[argb] = brush;
        return brush;
    }
}
