using System;
using System.Collections.Generic;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>
/// PLACEHOLDER — hardcoded data that looks like real scan output, so the UI
/// shows a realistic picture before the ProjectScanner lands (Phase 0).
///
/// Rules already encoded here, mirrored in the plan for the real scanner:
///  * Scan root is <see cref="ScanRoot"/> (the DevEcosystem folder).
///  * DevDesk itself is NOT part of <see cref="GetAll"/> — it has its own
///    dedicated page fed by <see cref="GetSelf"/>.
///  * Only git repos directly under the scan root count as projects.
/// </summary>
public static class SampleDataService
{
    /// <summary>Root folder the scanner must look inside.</summary>
    public const string ScanRoot = @"C:\Code\projects\DevEcosystem";

    /// <summary>The only other project next to DevDesk inside the ecosystem.</summary>
    public static IReadOnlyList<ProjectModel> GetAll() => new List<ProjectModel>
    {
        new ProjectModel
        {
            Name = "DevTem WinUI 3",
            Path = @"C:\Code\projects\DevEcosystem\DevTem WinUI 3",
            IconGlyph = "\uE950",
            Branch = "main",
            Version = "0.0.2",
            HasRemote = true,
            RemoteOk = true,
            RemoteHost = "GitHub",
            StatusText = "Clean working tree",
            RecentCommits = new List<CommitInfo>
            {
                new() { Subject = "Add AGENTS.md and NEXT-DEV.md roadmap", Author = "fettah", Date = "2 d ago", Hash = "4596596" },
                new() { Subject = "Fix vpk pack icon flag for Velopack v1 CLI (--icon)", Author = "fettah", Date = "3 d ago", Hash = "41d2ad2" },
                new() { Subject = "Add auto-update on startup, crash logging, app icon (v0.0.2)", Author = "fettah", Date = "4 d ago", Hash = "3b32212" },
            },
            Components = new List<ComponentInfo>
            {
                new() { Name = "Template sources", RelativePath = @"DevTem Win UI 3", Kind = "sources" },
                new() { Name = "README", RelativePath = @"DevTem Win UI 3\README.md", Kind = "docs" },
            },
        },
    };

    /// <summary>
    /// DevDesk's own repo, shown on its dedicated page. Marked as a workspace:
    /// one repo containing several components (app, tests, docs, scripts).
    /// </summary>
    public static ProjectModel GetSelf() => new()
    {
        Name = "DevDesk",
        Path = ScanRoot + @"\DevDesk",
        IconGlyph = "\uE718",
        IsWorkspace = true,
        Branch = "main",
        Version = "0.0.1",
        HasRemote = false,
        RemoteOk = false,
        RemoteHost = "Local only",
        StatusText = "No remote configured",
        RecentCommits = new List<CommitInfo>
        {
            new() { Subject = "DevDesk UI shell (Phase 0): theme, navigation, shortcut launcher, tests", Author = "fettah", Date = "today", Hash = "0ae89f2" },
        },
        Components = new List<ComponentInfo>
        {
            new() { Name = "DevDesk app", RelativePath = @"DevDesk\DevDesk.csproj", Kind = "app" },
            new() { Name = "DevDesk.Tests", RelativePath = @"DevDesk\DevDesk.Tests\DevDesk.Tests.csproj", Kind = "tests" },
            new() { Name = "DEVDESK-PLAN", RelativePath = @"DevDesk\docs\DEVDESK-PLAN.md", Kind = "docs" },
            new() { Name = "Scripts", RelativePath = @"DevDesk\Scripts", Kind = "scripts" },
            new() { Name = "CI workflow", RelativePath = @"DevDesk\.github\workflows\release.yml", Kind = "workflow" },
        },
    };
}