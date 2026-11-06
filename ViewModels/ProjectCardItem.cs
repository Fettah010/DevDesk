using System;
using System.Collections.Generic;
using DevDesk.Models;
using DevDesk.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DevDesk.ViewModels;

/// <summary>
/// A Dashboard card: the scan result plus the last run remembered for that
/// project (W-0.3.7).
/// <para>
/// The join happens here, never inside <see cref="ProjectModel"/>: a scan is a
/// fact about the folder now, and a build is a fact about a process that ran
/// earlier (D-0.3.1). A project that has never built says so, instead of showing
/// a neighbouring number (gotcha 16).
/// </para>
/// </summary>
public sealed class ProjectCardItem
{
    public ProjectCardItem(ProjectModel model, BuildRecord? lastBuild, ProjectOrganizationEntry? organization = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        LastBuild = lastBuild;
        Organization = organization;
    }

    public ProjectModel Model { get; }

    public BuildRecord? LastBuild { get; }

    public ProjectOrganizationEntry? Organization { get; }

    public bool HasTags => Organization?.Tags.Count > 0;

    public string TagsText => Organization is { Tags.Count: > 0 } organization
        ? string.Join(" · ", organization.Tags)
        : string.Empty;

    public string TagsAutomationName => $"Project tags: {TagsText}";

    public SolidColorBrush TagsBrush => Organization?.TagColor switch
    {
        "Teal" => new SolidColorBrush(Color.FromArgb(255, 0, 112, 116)),
        "Purple" => new SolidColorBrush(Color.FromArgb(255, 112, 65, 160)),
        "Orange" => new SolidColorBrush(Color.FromArgb(255, 168, 82, 0)),
        _ => new SolidColorBrush(Color.FromArgb(255, 45, 127, 249)),
    };

    // The card reads these directly; the scan model stays the single owner of
    // them (no copy is stored, every getter forwards).

    public string Name => Model.Name;

    public string Path => Model.Path;

    public string IconGlyph => Model.IconGlyph;

    public bool VersionMismatch => Model.VersionMismatch;

    public string VersionDisplay => Model.VersionDisplay;

    public bool IsWorkspace => Model.IsWorkspace;

    public bool IsWorktree => Model.IsWorktree;

    public bool DetailsPending => Model.DetailsPending;

    public string? WorktreeOfPath => Model.WorktreeOfPath;

    public bool RemoteOk => Model.RemoteOk;

    public bool IsNotRemoteOk => Model.IsNotRemoteOk;

    public string Branch => Model.Branch;

    public string StatusText => Model.StatusText;

    public IReadOnlyList<CommitInfo> RecentCommits => Model.RecentCommits;

    public string RemoteHost => Model.RemoteHost;

    public bool Dirty => Model.Dirty;

    public bool HasBuild => LastBuild is not null;

    /// <summary>"build ok · 1.24 s", "publish failed", or "no build yet".</summary>
    public string BuildBadgeText => LastBuild?.BadgeText ?? "no build yet";

    public string BuildGlyph => LastBuild?.Status switch
    {
        OperationStatus.Succeeded => "\uE73E",
        OperationStatus.Failed => "\uE783",
        OperationStatus.Canceled => "\uE71A",
        _ => "\uE823",
    };

    public Visibility OkVisibility => Visible(LastBuild?.Status == OperationStatus.Succeeded);

    public Visibility FailedVisibility => Visible(LastBuild?.Status == OperationStatus.Failed);

    /// <summary>Never built, or stopped: muted, because neither is a verdict.</summary>
    public Visibility MutedVisibility =>
        Visible(LastBuild is null || LastBuild.Status is OperationStatus.Canceled or OperationStatus.Queued or OperationStatus.Running);

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
