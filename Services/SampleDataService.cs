using System.Collections.Generic;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>
/// PLACEHOLDER — hardcoded project data that looks like real scan output, so
/// the UI shows a realistic picture before the ProjectScanner lands (Phase 0).
/// Swap <c>GetAll()</c> for the real scanner when it exists.
/// </summary>
public static class SampleDataService
{
    public static IReadOnlyList<ProjectModel> GetAll() => new List<ProjectModel>
    {
        new ProjectModel
        {
            Name = "DevDesk",
            Path = @"C:\Code\projects\DevEcosystem\DevDesk",
            IconGlyph = "\uE950",
            Branch = "main",
            Version = "0.0.1",
            HasRemote = true,
            RemoteOk = true,
            RemoteHost = "GitHub",
            StatusText = "Clean working tree",
            RecentCommits = new List<CommitInfo>
            {
                new() { Subject = "UI shell: theme, navigation and dashboard", Author = "fettah", Date = "12 min ago", Hash = "2f1a9c4" },
                new() { Subject = "Copy template scaffolding into DevDesk", Author = "fettah", Date = "1 h ago", Hash = "8ec0b3d" },
                new() { Subject = "Bump version to 0.0.1", Author = "fettah", Date = "2 h ago", Hash = "d35e91a" },
            }
        },
        new ProjectModel
        {
            Name = "DevTem-WinUI 3",
            Path = @"C:\Code\projects\DevEcosystem\MyWinUIApp",
            Branch = "main",
            Version = "0.0.2",
            HasRemote = true,
            RemoteOk = true,
            RemoteHost = "GitHub",
            Dirty = true,
            BehindUpstream = true,
            StatusText = "Dirty · 2 commits behind",
            RecentCommits = new List<CommitInfo>
            {
                new() { Subject = "Add AGENTS.md and NEXT-DEV.md roadmap", Author = "fettah", Date = "4 h ago", Hash = "4596596" },
                new() { Subject = "Fix vpk pack icon flag for Velopack v1 CLI (--icon)", Author = "fettah", Date = "1 d ago", Hash = "41d2ad2" },
                new() { Subject = "Add auto-update on startup, crash logging, app icon (v0.0.2)", Author = "fettah", Date = "2 d ago", Hash = "3b32212" },
            }
        },
        new ProjectModel
        {
            Name = "PIgen App",
            Path = @"C:\Code\projects\VS studio projects\PIgen App",
            IsWorkspace = true,
            Branch = "master",
            Version = "1.2.0",
            HasRemote = false,
            RemoteOk = false,
            RemoteHost = "Local only",
            StatusText = "No remote configured",
            RecentCommits = new List<CommitInfo>
            {
                new() { Subject = "Auto-start backend from the WinUI app when not already running", Author = "fettah", Date = "3 d ago", Hash = "4f544cf" },
                new() { Subject = "Separate backend/frontend: self-sufficient backend engine", Author = "fettah", Date = "5 d ago", Hash = "0ce4480" },
                new() { Subject = "Improve prompt quality: structured sectioned prompts", Author = "fettah", Date = "1 w ago", Hash = "e8a105e" },
            }
        },
    };
}