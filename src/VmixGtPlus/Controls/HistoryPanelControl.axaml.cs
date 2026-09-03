using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VmixGtPlus.Services;

namespace VmixGtPlus.Controls;

public partial class HistoryPanelControl : UserControl
{
    private HistoryService? _history;

    public HistoryPanelControl()
    {
        InitializeComponent();
    }

    public HistoryService? History
    {
        get => _history;
        set
        {
            if (_history is not null)
                _history.Changed -= OnHistoryChanged;
            _history = value;
            if (_history is not null)
                _history.Changed += OnHistoryChanged;
            Refresh();
        }
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        HistoryStack.Children.Clear();

        var actions = _history?.Actions;
        var cursor  = _history?.Cursor ?? -1;

        // "Original" state before any edits
        HistoryStack.Children.Add(BuildRow("(Original)", isCurrent: cursor == -1, isFuture: false));

        if (actions is null) return;

        for (int i = 0; i < actions.Count; i++)
        {
            bool isCurrent = i == cursor;
            bool isFuture  = i > cursor;
            HistoryStack.Children.Add(BuildRow(actions[i].Description, isCurrent, isFuture));
        }

        // scroll to bottom when at the newest state (no undone future items)
        if (_history is not null && _history.Cursor == _history.Actions.Count - 1)
            Scroller.ScrollToEnd();
    }

    private static Border BuildRow(string text, bool isCurrent, bool isFuture)
    {
        var fg = isFuture
            ? Color.Parse("#494949")
            : Color.Parse("#aaaaaa");

        var tb = new TextBlock
        {
            Text              = text,
            FontSize          = 11,
            Foreground        = new SolidColorBrush(fg),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        };

        if (isCurrent)
        {
            var marker = new TextBlock
            {
                Text              = "▶",
                FontSize          = 8,
                Foreground        = new SolidColorBrush(Color.Parse("#5090ff")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(6, 0, 5, 0),
            };

            var inner = new StackPanel { Orientation = Orientation.Horizontal };
            inner.Children.Add(marker);
            inner.Children.Add(tb);

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(45, 80, 140, 255)),
                Padding    = new Thickness(0, 4, 6, 4),
                Child      = inner,
            };
        }

        tb.Margin = new Thickness(19, 0, 4, 0);

        return new Border
        {
            Background = Brushes.Transparent,
            Padding    = new Thickness(0, 4, 6, 4),
            Child      = tb,
        };
    }
}
