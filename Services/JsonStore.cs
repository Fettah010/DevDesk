using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace DevDesk.Services;

/// <summary>Outcome of reading one JSON file.</summary>
public sealed record JsonLoadResult(JsonObject? Root, bool Corrupt, string? BackupPath, string? Error);

/// <summary>
/// A single small JSON document on disk, written so that a crash mid-write can
/// never leave a truncated file: the new content goes to a temp file in the same
/// directory and is then swapped in with <see cref="File.Replace(string, string, string?)"/>,
/// falling back to <see cref="File.Move(string, string, bool)"/> where Replace is
/// not supported (some network shares).
/// <para>
/// A file that does not parse is renamed to
/// <c>&lt;name&gt;.corrupt-&lt;timestamp&gt;.json</c> and reported, never
/// deleted and never thrown into startup.
/// </para>
/// </summary>
public sealed class JsonStore
{
    private readonly object _gate = new();

    public JsonStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new ArgumentException("The settings path has no directory.", nameof(path));
    }

    public string Path { get; }

    public string Directory { get; }

    public JsonLoadResult Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
            {
                return new JsonLoadResult(null, Corrupt: false, BackupPath: null, Error: null);
            }

            string text;
            try
            {
                text = File.ReadAllText(Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new JsonLoadResult(null, Corrupt: false, BackupPath: null, Error: ex.Message);
            }

            try
            {
                var root = JsonNode.Parse(text) as JsonObject
                    ?? throw new JsonException("The document root is not an object.");
                return new JsonLoadResult(root, Corrupt: false, BackupPath: null, Error: null);
            }
            catch (JsonException ex)
            {
                var backup = BackupCorrupt();
                return new JsonLoadResult(null, Corrupt: true, BackupPath: backup, Error: ex.Message);
            }
        }
    }

    /// <summary>Writes <paramref name="root"/> atomically. Throws only if the disk truly refuses.</summary>
    public void Save(JsonObject root)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory);

            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var temp = Path + ".tmp";

            try
            {
                File.WriteAllText(temp, json);
                Swap(temp, Path);

                var written = File.ReadAllText(Path);
                if (!string.Equals(written, json, StringComparison.Ordinal))
                {
                    throw new IOException($"Verification failed after writing {Path}.");
                }
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (IOException)
                    {
                        // A leftover temp file is harmless; the next save overwrites it.
                    }
                }
            }
        }
    }

    private static void Swap(string temp, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(temp, destination);
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Replace(temp, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(20); // another process (a second instance) is holding it
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Replace is not supported everywhere (network shares, some filters).
                File.Move(temp, destination, overwrite: true);
                return;
            }
        }
    }

    private string? BackupCorrupt()
    {
        var backup = System.IO.Path.Combine(
            Directory,
            $"{System.IO.Path.GetFileNameWithoutExtension(Path)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        try
        {
            File.Move(Path, backup, overwrite: true);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.Log.Warning(ex, "Could not back up the corrupt file {Path}", Path);
            return null;
        }
    }
}
