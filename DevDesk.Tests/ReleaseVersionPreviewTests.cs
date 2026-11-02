using System;
using System.IO;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class ReleaseVersionPreviewTests
{
    [Theory]
    [InlineData(ReleaseChannel.Stable, "1.0.1", "v1.0.1", "1.0.1")]
    [InlineData(ReleaseChannel.Beta, "0.6.1", "v0.6.1-beta", "0.6.1-beta")]
    [InlineData(ReleaseChannel.Dev, "0.6.1", "v0.6.1-dev", "0.6.1-dev")]
    public void TryCreate_PreparesFourFieldDiffWithoutWriting(
        ReleaseChannel channel,
        string proposedVersion,
        string expectedTag,
        string expectedInformationalVersion)
    {
        using var project = new TemporaryProject();
        project.Write("0.6.0", "0.6.0-beta");
        var original = project.ReadText();
        var suggestion = Suggestion("0.6.0", proposedVersion, channel, expectedTag);

        var succeeded = ReleaseVersionPreviewBuilder.TryCreate(
            project.File,
            suggestion,
            out var preview,
            out var error);

        Assert.True(succeeded, error);
        Assert.NotNull(preview);
        Assert.Equal("0.6.0", preview.CurrentVersion);
        Assert.Equal(proposedVersion, preview.ProposedVersion);
        Assert.Equal(expectedTag, preview.Tag);
        Assert.Equal(4, preview.Changes.Count);
        Assert.Contains($"InformationalVersion>{expectedInformationalVersion}", preview.Diff);
        Assert.Equal(original, project.ReadText());
    }

    [Fact]
    public void TryCreate_AllowsAChannelTagBaselineDifferentFromTheCurrentProjectVersion()
    {
        using var project = new TemporaryProject();
        project.Write("0.3.0", "0.3.0-beta");

        var succeeded = ReleaseVersionPreviewBuilder.TryCreate(
            project.File,
            Suggestion("0.5.0", "0.5.1", ReleaseChannel.Beta, "v0.5.1-beta"),
            out var preview,
            out var error);

        Assert.True(succeeded, error);
        Assert.Equal("0.3.0", preview!.CurrentVersion);
        Assert.Equal("0.5.1", preview.ProposedVersion);
    }

    [Fact]
    public void TryCreate_RefusesSuggestionThatWouldDowngradeCurrentProjectVersion()
    {
        using var project = new TemporaryProject();
        project.Write("0.6.3", "0.6.3-beta");

        var succeeded = ReleaseVersionPreviewBuilder.TryCreate(
            project.File,
            Suggestion("0.6.1", "0.6.2", ReleaseChannel.Beta, "v0.6.2-beta"),
            out var preview,
            out var error);

        Assert.False(succeeded);
        Assert.Null(preview);
        Assert.Contains("greater than the project's current", error);
    }

    [Theory]
    [InlineData("0.5.0.0", "0.6.0-beta")]
    [InlineData("0.6.0.0", "0.6.1-beta")]
    [InlineData("0.6.0.0", "0.6.1-alpha")]
    public void TryCreate_RefusesAnInconsistentCurrentVersionQuartet(
        string assemblyVersion,
        string informationalVersion)
    {
        using var project = new TemporaryProject();
        project.Write("0.6.0", informationalVersion, assemblyVersion: assemblyVersion);

        var succeeded = ReleaseVersionPreviewBuilder.TryCreate(
            project.File,
            Suggestion("0.6.0", "0.6.1", ReleaseChannel.Beta, "v0.6.1-beta"),
            out var preview,
            out var error);

        Assert.False(succeeded);
        Assert.Null(preview);
        Assert.Contains("four version fields", error);
    }

    [Theory]
    [InlineData("v0.6.1-dev")]
    [InlineData("v0.6.2-beta")]
    [InlineData("v0.6.1-beta.1")]
    public void TryCreate_RefusesTagThatDoesNotMatchTheSuggestion(
        string tag)
    {
        using var project = new TemporaryProject();
        project.Write("0.6.0", "0.6.0-beta");
        var suggestion = Suggestion("0.6.0", "0.6.1", ReleaseChannel.Beta, tag);

        var succeeded = ReleaseVersionPreviewBuilder.TryCreate(
            project.File,
            suggestion,
            out var preview,
            out var error);

        Assert.False(succeeded);
        Assert.Null(preview);
        Assert.Contains("tag", error);
    }

    [Theory]
    [InlineData("V0.6.1-beta")]
    [InlineData("v0.6.1-BETA")]
    [InlineData("0.6.1-beta")]
    public void TryCreate_RefusesNonCanonicalReleaseTagSpelling(string tag)
    {
        using var project = new TemporaryProject();
        project.Write("0.6.0", "0.6.0-beta");

        var succeeded = ReleaseVersionPreviewBuilder.TryCreate(
            project.File,
            Suggestion("0.6.0", "0.6.1", ReleaseChannel.Beta, tag),
            out var preview,
            out var error);

        Assert.False(succeeded);
        Assert.Null(preview);
        Assert.Contains("tag", error);
    }

    [Fact]
    public void TryCreate_RejectsMissingVersionField()
    {
        using var project = new TemporaryProject();
        project.Write("0.6.0", "0.6.0-beta");
        project.WriteText(project.ReadText().Replace(
            "    <Version>0.6.0</Version>\n",
            string.Empty,
            StringComparison.Ordinal));

        var succeeded = ReleaseVersionPreviewBuilder.TryCreate(
            project.File,
            Suggestion("0.6.0", "0.6.1", ReleaseChannel.Beta, "v0.6.1-beta"),
            out var preview,
            out var error);

        Assert.False(succeeded);
        Assert.Null(preview);
        Assert.Contains("<Version>", error);
    }

    private static ReleaseVersionSuggestion Suggestion(
        string baseline,
        string version,
        ReleaseChannel channel,
        string tag) =>
        new(baseline, version, ReleaseBumpKind.Patch, channel, tag);

    private sealed class TemporaryProject : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), "devdesk-release-preview-" + Guid.NewGuid().ToString("N"));

        public TemporaryProject() => Directory.CreateDirectory(_directory);

        public string File => Path.Combine(_directory, "DevDesk.csproj");

        public void Write(
            string version,
            string informationalVersion,
            string? assemblyVersion = null,
            string? fileVersion = null)
        {
            WriteText(
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                "  <PropertyGroup>\n" +
                $"    <Version>{version}</Version>\n" +
                $"    <AssemblyVersion>{assemblyVersion ?? version + ".0"}</AssemblyVersion>\n" +
                $"    <FileVersion>{fileVersion ?? version + ".0"}</FileVersion>\n" +
                $"    <InformationalVersion>{informationalVersion}</InformationalVersion>\n" +
                "  </PropertyGroup>\n" +
                "</Project>\n");
        }

        public string ReadText() => System.IO.File.ReadAllText(File);

        public void WriteText(string value) => System.IO.File.WriteAllText(File, value);

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
