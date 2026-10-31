using System;
using DevDesk.Services;
using DevDesk.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevDesk.Pages;

/// <summary>
/// The Activity page (W-0.3.6). The centre raises <c>Changed</c> on whatever
/// thread finished an operation, so this page is the only place that knows about
/// the dispatcher; <see cref="ActivityViewModel"/> stays synchronous and testable.
/// </summary>
public sealed partial class ActivityPage : Page
{
    private readonly ActivityCenter _center = AppServices.Activity;
    private bool _listening;

    public ActivityPage()
    {
        ViewModel = new ActivityViewModel(_center);
        InitializeComponent();

        Loaded += ActivityPage_Loaded;
        Unloaded += ActivityPage_Unloaded;
    }

    public ActivityViewModel ViewModel { get; }

    private void ActivityPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_listening)
        {
            _center.Changed += Center_Changed;
            _listening = true;
        }

        // Navigation keeps no page cache, so take a fresh snapshot every entry.
        ViewModel.Load();
    }

    private void ActivityPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_listening)
        {
            _center.Changed -= Center_Changed;
            _listening = false;
        }
    }

    private void Center_Changed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(ViewModel.Load);

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not { } id)
        {
            return;
        }

        if (!ViewModel.Cancel(id))
        {
            ShowWarning("Already finished", "That run ended before the stop reached it.");
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not { } id)
        {
            return;
        }

        if (ViewModel.Retry(id) is null)
        {
            ShowWarning(
                "Cannot retry this run",
                "Its command belongs to an earlier launch of DevDesk. Start it again from the project's Build tab.");
        }
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not { } id || ViewModel.Find(id) is not { } row)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.LogPath) || !ProjectOpenActions.OpenWithDefaultApp(row.LogPath))
        {
            ShowWarning("No log for this run", "The log file is no longer on disk; DevDesk keeps the path, not the text.");
        }
    }

    private static string? TagOf(object sender) =>
        (sender as FrameworkElement)?.Tag as string;

    private void ShowWarning(string title, string message)
    {
        PageInfoBar.Severity = InfoBarSeverity.Warning;
        PageInfoBar.Title = title;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }
}
