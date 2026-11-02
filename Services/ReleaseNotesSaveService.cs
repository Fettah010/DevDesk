using System;
using System.IO;
using System.Text;
using System.Threading;

namespace DevDesk.Services;

public sealed record ReleaseNotesSaveResult(bool Succeeded, string Message, string? Path = null);

public interface IReleaseNotesSaveService
{
    ReleaseNotesSaveResult SaveConfirmed(
        string repositoryPath,
        string targetPath,
        string content,
        bool confirmed,
        bool overwriteConfirmed,
        CancellationToken cancellationToken = default);
}

public sealed class ReleaseNotesSaveService : IReleaseNotesSaveService
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static string DefaultTargetPath(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        return Path.Combine(Path.GetFullPath(repositoryPath), "ReleaseNotes.md");
    }

    public ReleaseNotesSaveResult SaveConfirmed(
        string repositoryPath,
        string targetPath,
        string content,
        bool confirmed,
        bool overwriteConfirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);

        if (!confirmed)
        {
            return new ReleaseNotesSaveResult(false, "Saving release notes was not confirmed.");
        }

        if (!TryResolveTarget(repositoryPath, targetPath, out var fullPath, out var directory, out var error))
        {
            return new ReleaseNotesSaveResult(false, error);
        }

        if (File.Exists(fullPath) && !overwriteConfirmed)
        {
            return new ReleaseNotesSaveResult(
                false,
                "The selected Markdown file already exists. Review it and separately confirm replacement before saving.");
        }

        if (Directory.Exists(fullPath))
        {
            return new ReleaseNotesSaveResult(false, "The selected release-notes path is a directory.");
        }

        if (HasReparsePointInPath(repositoryPath, fullPath, out error))
        {
            return new ReleaseNotesSaveResult(false, error);
        }

        if (!Directory.Exists(directory))
        {
            return new ReleaseNotesSaveResult(false, "The selected release-notes folder does not exist.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ReleaseNotesSaveResult(false, "Saving release notes was cancelled.");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        ReleaseNotesSaveResult result;
        try
        {
            var bytes = Utf8WithoutBom.GetBytes(content);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullPath))
            {
                if (!overwriteConfirmed)
                {
                    return new ReleaseNotesSaveResult(
                        false,
                        "The selected Markdown file appeared after confirmation; review it before replacing.");
                }

                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }

            result = new ReleaseNotesSaveResult(true, "Release notes were saved.", fullPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new ReleaseNotesSaveResult(false, "Saving release notes was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result = new ReleaseNotesSaveResult(
                false,
                $"Release notes could not be saved atomically: {exception.Message}");
        }

        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ReleaseNotesSaveResult(
                false,
                $"{result.Message} The temporary file could not be cleaned up: {exception.Message}",
                result.Path);
        }

        return result;
    }

    private static bool TryResolveTarget(
        string repositoryPath,
        string targetPath,
        out string fullPath,
        out string directory,
        out string error)
    {
        fullPath = string.Empty;
        directory = string.Empty;
        error = string.Empty;
        try
        {
            var root = Path.GetFullPath(repositoryPath);
            fullPath = Path.GetFullPath(targetPath);
            directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            var relativePath = Path.GetRelativePath(root, fullPath);
            if (Path.IsPathRooted(relativePath)
                || relativePath is "." or ".."
                || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                error = "Release notes must be saved inside the selected repository.";
                return false;
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".md", StringComparison.OrdinalIgnoreCase))
            {
                error = "Release notes must be saved as a Markdown (.md) file.";
                return false;
            }

            if (!Directory.Exists(root))
            {
                error = "The selected repository folder is unavailable.";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = "The selected repository or release-notes path is invalid.";
            return false;
        }
    }

    private static bool HasReparsePointInPath(string repositoryPath, string targetPath, out string error)
    {
        error = string.Empty;
        var root = Path.GetFullPath(repositoryPath);
        var directory = Path.GetDirectoryName(targetPath);
        if (directory is null)
        {
            error = "The selected release-notes folder is invalid.";
            return true;
        }

        var relativeDirectory = Path.GetRelativePath(root, directory);
        var current = root;
        if (HasReparsePoint(current))
        {
            error = "Release notes cannot be written through a linked repository folder.";
            return true;
        }

        if (relativeDirectory != ".")
        {
            foreach (var segment in relativeDirectory.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (Directory.Exists(current) && HasReparsePoint(current))
                {
                    error = "Release notes cannot be written through a linked repository folder.";
                    return true;
                }
            }
        }

        if (File.Exists(targetPath) && HasReparsePoint(targetPath))
        {
            error = "Release notes cannot replace a linked file.";
            return true;
        }

        return false;
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
