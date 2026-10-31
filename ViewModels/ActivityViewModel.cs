using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DevDesk.Models;
using DevDesk.Services;
using Microsoft.UI.Xaml;

namespace DevDesk.ViewModels;

/// <summary>
/// The Activity page's state: this session's queue merged with the runs read back
/// from <c>build-history.json</c>, plus the two filters (W-0.3.6, D-0.3.8).
/// <para>
/// Synchronous on purpose. The centre raises <see cref="ActivityCenter.Changed"/>
/// on whatever thread finished an operation; the page owns the dispatcher and
/// calls <see cref="Load"/> there, which keeps the merge testable without a
/// window and keeps the UI thread in exactly one place.
/// </para>
/// </summary>
public sealed partial class ActivityViewModel : ObservableObject
{
    public const string AllProjects = "All projects";
    public const string AllKinds = "All kinds";

    private static readonly IReadOnlyList<string> KindChoices =
        new[] { AllKinds, "Build", "Run", "Publish", "Test" };

    private readonly ActivityCenter _center;
    private readonly BuildHistory _history;
    private List<ActivityRow> _all = new();

    [ObservableProperty]
    public partial ObservableCollection<ActivityRow> Rows { get; set; } = new();

    [ObservableProperty]
    public partial IReadOnlyList<string> Projects { get; set; } = new[] { AllProjects };

    [ObservableProperty]
    public partial IReadOnlyList<string> Kinds { get; set; } = KindChoices;

    [ObservableProperty]
    public partial string SelectedProject { get; set; } = AllProjects;

    [ObservableProperty]
    public partial string SelectedKind { get; set; } = AllKinds;

    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility EmptyVisibility { get; set; } = Visibility.Visible;

    public ActivityViewModel(ActivityCenter center, BuildHistory? history = null)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _history = history ?? center.History;
        RetentionText =
            $"The last {BuildHistory.DefaultLimit} runs are kept in build-history.json, so this list survives a restart. "
            + "Full logs stay on disk.";
    }

    public string RetentionText { get; }

    /// <summary>Rebuilds the merged list and reapplies the filters.</summary>
    public void Load()
    {
        var session = new List<ActivityRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var operation in _center.Operations)
        {
            session.Add(ActivityRow.FromOperation(operation));
            seen.Add(Key(operation.ProjectPath, operation.Kind, operation.StartedAt ?? operation.QueuedAt));
        }

        var stored = new List<ActivityRow>();
        var index = 0;
        foreach (var record in _history.Entries)
        {
            if (!seen.Contains(Key(record.ProjectPath, record.Kind, record.StartedAt)))
            {
                stored.Add(ActivityRow.FromRecord(record, index++));
            }
        }

        _all = session.Concat(stored).OrderByDescending(row => row.At).ToList();

        RefreshProjects();
        ApplyFilter();
    }

    /// <summary>Stops a running or queued operation. False when it already finished.</summary>
    public bool Cancel(string id) => _center.Cancel(id);

    /// <summary>Queues the same command again. Null when the request is not in this session.</summary>
    public OperationHandle? Retry(string id) => _center.Retry(id);

    public ActivityRow? Find(string? id) =>
        id is null ? null : _all.FirstOrDefault(row => string.Equals(row.Id, id, StringComparison.Ordinal));

    private void RefreshProjects()
    {
        var names = _all
            .Select(row => row.ProjectName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        names.Insert(0, AllProjects);
        Projects = names;

        if (!names.Contains(SelectedProject, StringComparer.OrdinalIgnoreCase))
        {
            SelectedProject = AllProjects;
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<ActivityRow> filtered = _all;

        if (!string.Equals(SelectedProject, AllProjects, StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(row =>
                string.Equals(row.ProjectName, SelectedProject, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(SelectedKind, AllKinds, StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(row =>
                string.Equals(row.KindLabel, SelectedKind, StringComparison.OrdinalIgnoreCase));
        }

        var rows = filtered.ToList();
        Rows = new ObservableCollection<ActivityRow>(rows);

        CountText = rows.Count == _all.Count
            ? $"{_all.Count} run{(_all.Count == 1 ? string.Empty : "s")}"
            : $"{rows.Count} of {_all.Count} runs";

        SummaryText = Summarize();
        EmptyVisibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText = _all.Count == 0
            ? "Nothing has run yet. Build, run, publish or test a project and it appears here — newest first."
            : "No run matches this filter. Choose another project or kind.";
    }

    private string Summarize()
    {
        var session = _all.Where(row => row.IsSession).ToList();
        if (session.Count == 0)
        {
            return _all.Count == 0
                ? "No activity this session."
                : "Nothing has run in this session; everything below was remembered from earlier launches.";
        }

        var parts = new List<string> { $"{session.Count} run{(session.Count == 1 ? string.Empty : "s")} this session" };
        Count(parts, session, OperationStatus.Running, "running");
        Count(parts, session, OperationStatus.Queued, "queued");
        Count(parts, session, OperationStatus.Succeeded, "succeeded");
        Count(parts, session, OperationStatus.Failed, "failed");
        Count(parts, session, OperationStatus.Canceled, "cancelled");
        return string.Join(" · ", parts);
    }

    private static void Count(List<string> parts, List<ActivityRow> rows, OperationStatus status, string label)
    {
        var count = rows.Count(row => row.Status == status);
        if (count > 0)
        {
            parts.Add($"{count} {label}");
        }
    }

    /// <summary>
    /// A stable identity for "the history already contains this run": project,
    /// kind and start. A finished session operation writes exactly that record,
    /// so the same run is never listed twice.
    /// </summary>
    private static string Key(string projectPath, OperationKind kind, DateTimeOffset at) =>
        $"{Normalize(projectPath)}|{kind}|{at.UtcTicks}";

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    partial void OnSelectedProjectChanged(string value) => ApplyFilter();

    partial void OnSelectedKindChanged(string value) => ApplyFilter();
}
