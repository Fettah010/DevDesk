using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DevDesk.Services;

/// <summary>
/// Binary-safe copy of a template tree to a target folder, with rollback.
/// <para>
/// Every file is copied byte-for-byte; every rename is recorded so a failed or
/// cancelled run can delete exactly what it created. The template itself is
/// never opened for writing.
/// </para>
/// </summary>
public sealed class TemplateCopyService
{
    /// <summary>Result of a copy: the target folder and every action taken.</summary>
    public sealed record CopyResult(
        string TargetPath,
        IReadOnlyList<string> FilesCopied,
        IReadOnlyList<RenamePlan> Renames,
        TimeSpan Duration,
        bool Cancelled)
    {
        public int FileCount => FilesCopied.Count;
    }

    /// <summary>Progress reported per file while copying.</summary>
    public sealed record Progress(string FileName, int FilesDone, int FilesTotal);

    private readonly TimeProvider _time;

    public TemplateCopyService(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Copies <paramref name="templateRoot"/> to <paramref name="targetPath"/>,
    /// applying <paramref name="tokens"/> to file content and renaming
    /// csproj/sln files per <paramref name="renamePlans"/>.
    /// <para>
    /// If <paramref name="cancellationToken"/> is cancelled, every file already
    /// created under <paramref name="targetPath"/> is deleted and the target
    /// folder itself is removed.
    /// </para>
    /// </summary>
    public async Task<CopyResult> CopyAsync(
        string templateRoot,
        string targetPath,
        TokenMap tokens,
        IReadOnlyList<RenamePlan> renamePlans,
        IProgress<Progress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var started = _time.GetTimestamp();
        var target = Path.GetFullPath(targetPath);
        var template = Path.GetFullPath(templateRoot);

        // Prepare the rename lookup: source file → target path.
        var renameLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in renamePlans)
        {
            renameLookup[plan.SourcePath] = plan.TargetPath;
        }

        // Gather every file under the template (excluding bin/obj/.vs etc).
        var allFiles = new List<string>();
        CollectFiles(template, allFiles, cancellationToken);

        var createdFiles = new List<string>();   // files we created (for rollback)
        var createdDirs = new List<string>();    // dirs we created (for rollback, deepest first)
        var copied = new List<string>();         // logical relative paths copied
        var renames = new List<RenamePlan>();    // renames actually applied

        try
        {
            for (var i = 0; i < allFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourceFile = allFiles[i];
                var relative = Path.GetRelativePath(template, sourceFile);

                // Determine target path: renamed if a plan exists, else same relative path.
                string targetFilePath;
                if (renameLookup.TryGetValue(sourceFile, out var plannedTarget))
                {
                    targetFilePath = plannedTarget;
                    renames.Add(new RenamePlan(sourceFile, targetFilePath));
                }
                else
                {
                    targetFilePath = Path.Combine(target, relative);
                }

                // Create the target directory if needed.
                var targetDir = Path.GetDirectoryName(targetFilePath)!;
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                    createdDirs.Add(targetDir);
                }

                // Read source bytes, apply token substitution, write target.
                var bytes = File.ReadAllBytes(sourceFile);
                var needsSubstitution = IsTextFile(sourceFile);
                var outputBytes = needsSubstitution
                    ? ApplyTokens(bytes, tokens)
                    : bytes;

                File.WriteAllBytes(targetFilePath, outputBytes);
                createdFiles.Add(targetFilePath);
                copied.Add(relative);

                progress?.Report(new Progress(
                    Path.GetFileName(sourceFile), i + 1, allFiles.Count));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var elapsed = _time.GetElapsedTime(started, _time.GetTimestamp());
            return new CopyResult(target, copied, renames, elapsed, Cancelled: false);
        }
        catch (OperationCanceledException)
        {
            // Rollback: delete every file we created, then every empty dir we
            // created (deepest first), then the target folder itself.
            Rollback(createdFiles, createdDirs, target);
            var elapsed = _time.GetElapsedTime(started, _time.GetTimestamp());
            return new CopyResult(target, copied, renames, elapsed, Cancelled: true);
        }
    }

    /// <summary>
    /// Whether <paramref name="filePath"/> is a text file that should receive
    /// token substitution. Binary files (.ico, .png, .dll) are copied as-is.
    /// </summary>
    private static bool IsTextFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".csproj" or ".sln" or ".cs" or ".xaml" or ".ps1" or ".manifest"
                or ".props" or ".targets" or ".config" or ".json" or ".xml"
                or ".cshtml" or ".razor" => true,
            _ => false,
        };
    }

    /// <summary>Applies token substitutions to the UTF-8 bytes of a text file.</summary>
    private static byte[] ApplyTokens(byte[] bytes, TokenMap tokens)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var replaced = tokens.Apply(text);
        return System.Text.Encoding.UTF8.GetBytes(replaced);
    }

    /// <summary>Collects every file under <paramref name="root"/>, skipping
    /// hygiene folders (bin, obj, .vs, .git, etc.) and reparse points.</summary>
    private static void CollectFiles(string root, List<string> files, CancellationToken cancellationToken)
    {
        foreach (var file in PathHygiene.SafeEnumerateFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(file);
        }

        foreach (var dir in PathHygiene.SafeEnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectFiles(dir, files, cancellationToken);
        }
    }

    /// <summary>Rolls back a cancelled/failed copy: deletes created files,
    /// then created directories (deepest first), then the target folder.</summary>
    private static void Rollback(
        List<string> createdFiles,
        List<string> createdDirs,
        string target)
    {
        // Delete files first.
        foreach (var file in createdFiles)
        {
            try { File.Delete(file); }
            catch { /* best effort */ }
        }

        // Delete directories deepest-first so parents become empty.
        foreach (var dir in createdDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: false);
                }
            }
            catch { /* best effort */ }
        }

        // Finally remove the target folder if it's now empty.
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: false);
            }
        }
        catch { /* best effort */ }
    }
}
