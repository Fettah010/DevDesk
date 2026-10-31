using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using DevDesk.Services;
using DevDesk.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace DevDesk.Controls;

/// <summary>
/// The build console (W-0.3.4): a live, bounded view of a build's output.
/// <para>
/// It is the <see cref="IProgress{T}"/> a build reports through, which is what
/// keeps the contract in one place: every line reaches the buffer, the file (the
/// service writes it), and the screen. Lines arrive on whatever thread the
/// process reader used, so they are queued and flushed on a 100 ms timer instead
/// of posting one dispatcher work item per line — a 100k-line build must not
/// freeze the window.
/// </para>
/// <para>
/// Search highlights the current match by selecting it and scrolling to it, and
/// says how many matches exist. Highlighting every match at once is not a
/// requirement the build path needs; not losing the line number is.
/// </para>
/// </summary>
public sealed partial class BuildConsole : UserControl, IProgress<ProcessOutputLine>
{
    private readonly BuildLogBuffer _buffer = new();
    private readonly ObservableCollection<LogLine> _visible = new();
    private readonly ConcurrentQueue<ProcessOutputLine> _pending = new();
    private readonly DispatcherQueueTimer _flushTimer;
    private int _searchIndex = -1;
    private bool _follow = true;

    public BuildConsole()
    {
        this.InitializeComponent();

        LinesList.ItemsSource = _visible;
        UpdateCounts();

        _flushTimer = DispatcherQueue.CreateTimer();
        _flushTimer.Interval = TimeSpan.FromMilliseconds(100);
        _flushTimer.IsRepeating = true;
        _flushTimer.Tick += (_, _) => Flush();

        Loaded += (_, _) => _flushTimer.Start();
        Unloaded += (_, _) =>
        {
            _flushTimer.Stop();
            Flush();
        };
    }

    /// <summary>The bounded window's contents, newest last.</summary>
    public IReadOnlyList<LogLine> Lines => _buffer.Items;

    public long DroppedCount => _buffer.DroppedCount;

    /// <summary>Queues a line. Called from the process reader's thread.</summary>
    public void Report(ProcessOutputLine value) => _pending.Enqueue(value);

    /// <summary>Empties the console. A new run starts from an empty screen.</summary>
    public void Clear()
    {
        while (_pending.TryDequeue(out _))
        {
        }

        _buffer.Clear();
        _visible.Clear();
        _searchIndex = -1;
        SearchStatus.Text = string.Empty;
        UpdateCounts();
    }

    /// <summary>Drains the queue into the buffer and the visible list. UI thread only.</summary>
    public void Flush()
    {
        var added = new List<LogLine>();
        var evicted = 0;

        while (_pending.TryDequeue(out var line))
        {
            var cleaned = new LogLine(AnsiText.ForConsole(line.Text), line.IsError, line.At);
            evicted += _buffer.Add(cleaned);
            added.Add(cleaned);
        }

        if (added.Count == 0)
        {
            return;
        }

        for (var index = 0; index < evicted && _visible.Count > 0; index++)
        {
            _visible.RemoveAt(0);
        }

        foreach (var line in added)
        {
            _visible.Add(line);
        }

        UpdateCounts();

        if (_follow && _visible.Count > 0)
        {
            LinesList.ScrollIntoView(_visible[^1]);
        }
    }

    private void UpdateCounts()
    {
        LineCount.Text = _buffer.Count == 1 ? "1 line" : $"{_buffer.Count:N0} lines";
        EmptyState.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_buffer.DroppedCount > 0)
        {
            DroppedNote.Text =
                $"{_buffer.DroppedCount:N0} earlier lines are not shown here. The full output is in the build log.";
            DroppedNote.Visibility = Visibility.Visible;
        }
        else
        {
            DroppedNote.Visibility = Visibility.Collapsed;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            FindNext();
            e.Handled = true;
        }
    }

    private void FindNext()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchStatus.Text = string.Empty;
            _searchIndex = -1;
            return;
        }

        var lines = _buffer.Items;
        var matches = 0;
        var next = -1;

        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches++;

            if (next < 0 && index > _searchIndex)
            {
                next = index;
            }
        }

        if (matches == 0)
        {
            SearchStatus.Text = "no matches";
            _searchIndex = -1;
            return;
        }

        // Wrap to the first match, so pressing Find next again keeps working.
        if (next < 0)
        {
            next = lines.ToList().FindIndex(line => line.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _searchIndex = next;
        SearchStatus.Text = $"{matches} match{(matches == 1 ? string.Empty : "es")}";

        if (next >= 0 && next < _visible.Count)
        {
            LinesList.SelectedIndex = next;
            LinesList.ScrollIntoView(_visible[next]);
        }
    }

    private void FollowToggle_Changed(object sender, RoutedEventArgs e)
    {
        _follow = FollowToggle.IsChecked == true;

        if (_follow && _visible.Count > 0)
        {
            LinesList.ScrollIntoView(_visible[^1]);
        }
    }

    private void WrapToggle_Changed(object sender, RoutedEventArgs e)
    {
        var key = WrapToggle.IsChecked == true ? "WrapSelector" : "NoWrapSelector";
        LinesList.ItemTemplateSelector = (DataTemplateSelector)Resources[key];
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = LinesList.SelectedItems.OfType<LogLine>().ToList();
        if (selected.Count > 0)
        {
            Copy(selected);
        }
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_buffer.IsEmpty)
        {
            Copy(_buffer.Items);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Clear();

    private static void Copy(IReadOnlyList<LogLine> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.AppendLine(line.Text);
        }

        var package = new DataPackage();
        package.SetText(text.ToString());
        Clipboard.SetContent(package);
    }
}
