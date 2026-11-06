using System;
using System.Collections.Generic;
using System.Linq;
using DevDesk.Models;

namespace DevDesk.Services;

public enum ProjectSortOrder
{
    Name,
    Activity,
    RecentlyOpened,
    Health,
    Version,
}

/// <summary>Pure in-memory search and sorting for project-list surfaces.</summary>
public static class ProjectListQuery
{
    public static IReadOnlyList<ProjectModel> Apply(
        IEnumerable<ProjectModel> projects,
        string? query,
        ProjectSortOrder sortOrder,
        IReadOnlyDictionary<string, ProjectOrganizationEntry>? organization = null,
        IReadOnlyList<ScanRootSettings>? roots = null,
        bool hideArchived = false)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var term = query?.Trim();
        var rootOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (roots is not null)
        {
            for (var index = 0; index < roots.Count; index++)
            {
                rootOrder.TryAdd(roots[index].Path, index);
            }
        }
        var filtered = projects.Where(project =>
            (!hideArchived || GetOrganization(project, organization)?.IsArchived != true)
            && Matches(project, term, organization));
        int RootRank(ProjectModel project) =>
            rootOrder.TryGetValue(project.ScanRootPath, out var index) ? index : int.MaxValue;

        var ordered = sortOrder switch
        {
            ProjectSortOrder.Activity => filtered
                .OrderByDescending(project => project.LastActivity.HasValue)
                .ThenByDescending(project => project.LastActivity)
                .ThenBy(RootRank)
                .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ProjectSortOrder.RecentlyOpened => filtered
                .OrderByDescending(project => GetOrganization(project, organization)?.LastOpened.HasValue ?? false)
                .ThenByDescending(project => GetOrganization(project, organization)?.LastOpened)
                .ThenBy(RootRank)
                .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ProjectSortOrder.Health => filtered
                .OrderBy(project => IsHealthy(project) ? 0 : 1)
                .ThenBy(RootRank)
                .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ProjectSortOrder.Version => filtered
                .OrderByDescending(project => VersionKey(project.Version), VersionKeyComparer.Instance)
                .ThenBy(RootRank)
                .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _ => filtered
                .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(RootRank)
                .ThenBy(project => project.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };

        return organization is null
            ? ordered
            : ordered
                .OrderByDescending(project => GetOrganization(project, organization)?.IsPinned ?? false)
                .ToArray();
    }

    private static ProjectOrganizationEntry? GetOrganization(
        ProjectModel project,
        IReadOnlyDictionary<string, ProjectOrganizationEntry>? organization) =>
        organization is not null && organization.TryGetValue(project.Path, out var entry)
            ? entry
            : null;

    private static bool Matches(
        ProjectModel project,
        string? term,
        IReadOnlyDictionary<string, ProjectOrganizationEntry>? organization) =>
        string.IsNullOrEmpty(term)
        || Contains(project.Name, term)
        || Contains(project.Path, term)
        || Contains(project.Branch, term)
        || Contains(project.Version, term)
        || (GetOrganization(project, organization)?.Tags.Any(tag => Contains(tag, term)) ?? false);

    private static bool Contains(string value, string term) =>
        value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static bool IsHealthy(ProjectModel project) =>
        project.RemoteOk && !project.Dirty && !project.BehindUpstream;

    private static (Version Version, bool Stable, string Suffix)? VersionKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "—")
        {
            return null;
        }

        var parsed = VersionInfo.Parse(value);
        if (!Version.TryParse(parsed.Version, out var version))
        {
            return null;
        }

        return (version, !parsed.IsPrerelease, parsed.Suffix);
    }

    private sealed class VersionKeyComparer : IComparer<(Version Version, bool Stable, string Suffix)?>
    {
        public static VersionKeyComparer Instance { get; } = new();

        public int Compare(
            (Version Version, bool Stable, string Suffix)? left,
            (Version Version, bool Stable, string Suffix)? right)
        {
            if (left is null)
            {
                return right is null ? 0 : -1;
            }

            if (right is null)
            {
                return 1;
            }

            var version = left.Value.Version.CompareTo(right.Value.Version);
            if (version != 0)
            {
                return version;
            }

            if (left.Value.Stable != right.Value.Stable)
            {
                return left.Value.Stable.CompareTo(right.Value.Stable);
            }

            return string.Compare(left.Value.Suffix, right.Value.Suffix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
