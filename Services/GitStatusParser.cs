using System;
using System.Collections.Generic;
using System.Globalization;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>Parses NUL-delimited porcelain v2 status without splitting file paths.</summary>
public static class GitStatusParser
{
    public static GitRepositoryStatus Parse(string output)
    {
        string? branch = null;
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var files = new List<GitFileStatus>();
        var expectingOriginalPath = false;

        foreach (var record in (output ?? string.Empty).Split('\0'))
        {
            if (record.Length == 0) continue;
            if (expectingOriginalPath)
            {
                var previous = files[^1];
                files[^1] = previous with { OriginalPath = record };
                expectingOriginalPath = false;
                continue;
            }
            var line = record;
            while (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var headerEnd = line.IndexOf('\n');
                var header = headerEnd < 0 ? line : line[..headerEnd];
                if (header.StartsWith("# branch.head ", StringComparison.Ordinal))
                    branch = header[14..] == "(detached)" ? null : header[14..];
                else if (header.StartsWith("# branch.upstream ", StringComparison.Ordinal))
                    upstream = header[18..];
                else if (header.StartsWith("# branch.ab ", StringComparison.Ordinal))
                    ParseAheadBehind(header[12..], out ahead, out behind);
                if (headerEnd < 0)
                {
                    line = string.Empty;
                    break;
                }
                line = line[(headerEnd + 1)..];
            }

            if (line.Length == 0) continue;
            var type = line[0];
            if (type == '2')
            {
                var fields = line.Split(' ', 10, StringSplitOptions.None);
                if (fields.Length == 10)
                {
                    files.Add(new GitFileStatus("renamed", fields[1][..1], fields[1][1..], fields[9]));
                    expectingOriginalPath = true;
                }
                continue;
            }
            if (type == '?')
            {
                files.Add(new GitFileStatus("untracked", "?", "?", line.Length > 2 ? line[2..] : string.Empty));
                continue;
            }
            if (type is '1' or 'u')
            {
                var fields = line.Split(' ', type == '1' ? 9 : 11, StringSplitOptions.None);
                var pathIndex = type == '1' ? 8 : 10;
                if (fields.Length > pathIndex)
                {
                    files.Add(new GitFileStatus(type == 'u' ? "conflict" : "changed", fields[1][..1], fields[1][1..], fields[pathIndex]));
                }
            }

        }

        return new GitRepositoryStatus(branch, upstream, ahead, behind, files);
    }

    private static void ParseAheadBehind(string value, out int ahead, out int behind)
    {
        ahead = 0;
        behind = 0;
        foreach (var item in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (item.Length < 2 || !int.TryParse(item.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) continue;
            if (item[0] == '+') ahead = count;
            if (item[0] == '-') behind = count;
        }
    }
}
