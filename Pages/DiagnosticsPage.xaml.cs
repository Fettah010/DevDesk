using System;
using DevDesk.Models;
using DevDesk.Services;
using DevDesk.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DevDesk.Pages;

/// <summary>
/// The Diagnostics page. The ring buffer is filled on whatever thread logged, so
/// this page is the only place that knows about the dispatcher: it marshals live
/// events onto the UI thread and leaves <see cref="DiagnosticsViewModel"/>
/// synchronous and testable.
/// </summary>
public sealed partial class DiagnosticsPage : Page
{
    private readonly LogRingBufferSink _ring = LoggingService.Ring;
    private readonly DispatcherQueueTimer _filterTimer;
    private bool _listening;

    public DiagnosticsPage()
    {
        ViewModel = new DiagnosticsViewModel(_ring);
        InitializeComponent();

        LogPathText.Text = LoggingService.LogDirectoryPath;
        RetentionText.Text = $"Last {LoggingService.RetainedFileCount} daily files kept";

        _filterTimer = DispatcherQueue.CreateTimer();
        _filterTimer.Interval = TimeSpan.FromMilliseconds(250);
        _filterTimer.IsRepeating = false;
        _filterTimer.Tick += FilterTimer_Tick;

        Loaded += DiagnosticsPage_Loaded;
        Unloaded += DiagnosticsPage_Unloaded;
    }

    public DiagnosticsViewModel ViewModel { get; }

    private void DiagnosticsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_listening)
        {
            _ring.Appended += Ring_Appended;
            _listening = true;
        }

        // The page can be re-entered (navigation keeps no cache), so take a fresh
        // snapshot rather than showing whatever the previous instance left.
        ViewModel.Load();
        ScrollToEnd();
    }

    private void DiagnosticsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_listening)
        {
            _ring.Appended -= Ring_Appended;
            _listening = false;
        }

        _filterTimer.Stop();
    }

    private void Ring_Appended(object? sender, LogEntry entry) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.Append(entry);
            ScrollToEnd();
        });

    private void ScrollToEnd()
    {
        if (!ViewModel.AutoFollow || ViewModel.Entries.Count == 0)
        {
            return;
        }

        LogList.ScrollIntoView(ViewModel.Entries[^1]);
    }

    // The text filter is debounced: typing "scan" must not rebuild a 2000-entry
    // list four times.
    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
        {
            return;
        }

        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void FilterTimer_Tick(DispatcherQueueTimer sender, object args) =>
        ViewModel.TextFilter = FilterBox.Text;

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = ViewModel.VisibleText();
            if (string.IsNullOrEmpty(text))
            {
                ViewModel.Report("Nothing to copy", "No event matches the current filter.");
                return;
            }

            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);

            ViewModel.Report("Copied", $"{ViewModel.Entries.Count} line(s) on the clipboard.");
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "Copying the visible log lines failed");
            ViewModel.Report("Copy failed", ex.Message);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ExportCommand.ExecuteAsync(DiagnosticsExport.DefaultDestination());
            RevealExport();
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "Diagnostics export failed");
            ViewModel.Report("Export failed", ex.Message);
        }
    }

    private void ShowExportButton_Click(object sender, RoutedEventArgs e) => RevealExport();

    private void RevealExport()
    {
        if (string.IsNullOrEmpty(ViewModel.ExportPath))
        {
            return;
        }

        ProjectOpenActions.RevealInExplorer(ViewModel.ExportPath);
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ProjectOpenActions.OpenInExplorer(LoggingService.LogDirectoryPath))
        {
            ViewModel.Report("Could not open the folder", LoggingService.LogDirectoryPath);
        }
    }
}
