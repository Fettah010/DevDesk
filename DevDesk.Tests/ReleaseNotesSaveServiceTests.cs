using System;
using System.IO;
using System.Linq;
using System.Text;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class ReleaseNotesSaveServiceTests
{
    [Fact]
    public void SaveConfirmed_RequiresExplicitSaveConfirmation()
    {
        using var sandbox = new NotesSandbox();
        var target = sandbox.File("ReleaseNotes.md");

        var result = sandbox.Service.SaveConfirmed(
            sandbox.Root,
            target,
            "# Release",
            confirmed: false,
            overwriteConfirmed: false);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(target));
        Assert.Empty(sandbox.TemporaryFiles);
    }

    [Fact]
    public void SaveConfirmed_CreatesMarkdownAtomicallyWithoutBom()
    {
        using var sandbox = new NotesSandbox();
        var target = sandbox.File("release-notes", "ReleaseNotes.md");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var result = sandbox.Service.SaveConfirmed(
            sandbox.Root,
            target,
            "# DevDesk 0.6.1-beta\n\n- A clean change\n",
            confirmed: true,
            overwriteConfirmed: false);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Path.GetFullPath(target), result.Path);
        Assert.Equal("# DevDesk 0.6.1-beta\n\n- A clean change\n", File.ReadAllText(target));
        Assert.False(File.ReadAllBytes(target).Take(3).SequenceEqual(Encoding.UTF8.GetPreamble()));
        Assert.Empty(sandbox.TemporaryFiles);
    }

    [Fact]
    public void SaveConfirmed_RequiresSeparateConfirmationToReplaceExistingFile()
    {
        using var sandbox = new NotesSandbox();
        var target = sandbox.File("ReleaseNotes.md");
        File.WriteAllText(target, "Existing notes");

        var refused = sandbox.Service.SaveConfirmed(
            sandbox.Root,
            target,
            "Updated notes",
            confirmed: true,
            overwriteConfirmed: false);

        Assert.False(refused.Succeeded);
        Assert.Contains("separately confirm replacement", refused.Message);
        Assert.Equal("Existing notes", File.ReadAllText(target));

        var replaced = sandbox.Service.SaveConfirmed(
            sandbox.Root,
            target,
            "Updated notes",
            confirmed: true,
            overwriteConfirmed: true);

        Assert.True(replaced.Succeeded, replaced.Message);
        Assert.Equal("Updated notes", File.ReadAllText(target));
        Assert.Empty(sandbox.TemporaryFiles);
    }

    [Fact]
    public void SaveConfirmed_RefusesPathsOutsideRepositoryAndNonMarkdownFiles()
    {
        using var sandbox = new NotesSandbox();
        var outside = Path.Combine(Path.GetTempPath(), "external-release-notes-" + Guid.NewGuid().ToString("N") + ".md");
        var wrongExtension = sandbox.File("ReleaseNotes.txt");

        var outsideResult = sandbox.Service.SaveConfirmed(
            sandbox.Root,
            outside,
            "Notes",
            confirmed: true,
            overwriteConfirmed: false);
        var extensionResult = sandbox.Service.SaveConfirmed(
            sandbox.Root,
            wrongExtension,
            "Notes",
            confirmed: true,
            overwriteConfirmed: false);

        Assert.False(outsideResult.Succeeded);
        Assert.Contains("inside the selected repository", outsideResult.Message);
        Assert.False(extensionResult.Succeeded);
        Assert.Contains("Markdown", extensionResult.Message);
        Assert.False(File.Exists(outside));
        Assert.False(File.Exists(wrongExtension));
    }

    [Fact]
    public void SaveConfirmed_RefusesMissingFoldersAndCancellation()
    {
        using var sandbox = new NotesSandbox();
        var missingFolder = sandbox.File("not-created", "ReleaseNotes.md");
        using var cancellation = new System.Threading.CancellationTokenSource();
        cancellation.Cancel();
        var missing = sandbox.Service.SaveConfirmed(
            sandbox.Root,
            missingFolder,
            "Notes",
            confirmed: true,
            overwriteConfirmed: false);
        var cancelled = sandbox.Service.SaveConfirmed(
            sandbox.Root,
            sandbox.File("ReleaseNotes.md"),
            "Notes",
            confirmed: true,
            overwriteConfirmed: false,
            cancellation.Token);

        Assert.False(missing.Succeeded);
        Assert.Contains("folder does not exist", missing.Message);
        Assert.False(cancelled.Succeeded);
        Assert.Contains("cancelled", cancelled.Message);
        Assert.Empty(sandbox.TemporaryFiles);
    }

    private sealed class NotesSandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "devdesk-release-notes-" + Guid.NewGuid().ToString("N"));
        public ReleaseNotesSaveService Service { get; } = new();
        public string[] TemporaryFiles => Directory.GetFiles(Root, "*.tmp", SearchOption.AllDirectories);

        public NotesSandbox() => Directory.CreateDirectory(Root);

        public string File(params string[] parts) => parts.Aggregate(Root, Path.Combine);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
