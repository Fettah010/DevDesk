using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class ReleaseVersionApplyServiceTests
{
    [Fact]
    public async Task ApplyConfirmedAsync_DeclinedConfirmationDoesNotStartProcess()
    {
        using var sandbox = new ReleaseSandbox();
        var runner = new FakeProcessRunner();
        var preview = sandbox.CreatePreview();
        var original = System.IO.File.ReadAllText(preview.ProjectFile);

        var result = await sandbox.CreateService(runner).ApplyConfirmedAsync(preview, confirmed: false);

        Assert.False(result.Succeeded);
        Assert.Empty(runner.Requests);
        Assert.Equal(original, System.IO.File.ReadAllText(preview.ProjectFile));
    }

    [Fact]
    public async Task ApplyConfirmedAsync_UsesHiddenArgumentListForExactPreview()
    {
        using var sandbox = new ReleaseSandbox();
        var runner = new FakeProcessRunner();
        var preview = sandbox.CreatePreview();
        runner.Before = (request, _) =>
        {
            var version = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "-Version") + 1];
            var suffix = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "-Suffix") + 1];
            var project = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "-Project") + 1];
            var informationalVersion = suffix == "none" ? version : $"{version}-{suffix}";
            System.IO.File.WriteAllText(project, ReleaseSandbox.ProjectXml(version, informationalVersion));
            return Task.CompletedTask;
        };

        var result = await sandbox.CreateService(runner).ApplyConfirmedAsync(preview, confirmed: true);

        Assert.True(result.Succeeded, result.Message);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("powershell.exe", request.FileName);
        Assert.Equal(sandbox.ToolingDirectory, request.WorkingDirectory);
        Assert.Equal(
            [
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                sandbox.ScriptPath,
                "-Version",
                "0.6.1",
                "-Suffix",
                "beta",
                "-Project",
                preview.ProjectFile
            ],
            request.Arguments);
    }

    [Fact]
    public async Task ApplyConfirmedAsync_DoesNotReportSuccessIfScriptDidNotApplyTheVersion()
    {
        using var sandbox = new ReleaseSandbox();
        var runner = new FakeProcessRunner();
        var preview = sandbox.CreatePreview();

        var result = await sandbox.CreateService(runner).ApplyConfirmedAsync(preview, confirmed: true);

        Assert.False(result.Succeeded);
        Assert.Contains("could not be verified", result.Message);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task ApplyConfirmedAsync_RefusesStalePreviewWithoutStartingProcess()
    {
        using var sandbox = new ReleaseSandbox();
        var runner = new FakeProcessRunner();
        var preview = sandbox.CreatePreview();
        System.IO.File.WriteAllText(
            preview.ProjectFile,
            ReleaseSandbox.ProjectXml("0.6.2", "0.6.2-beta"));

        var result = await sandbox.CreateService(runner).ApplyConfirmedAsync(preview, confirmed: true);

        Assert.False(result.Succeeded);
        Assert.Contains("changed after this preview", result.Message);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task ApplyConfirmedAsync_ReportsScriptFailureWithoutRetrying()
    {
        using var sandbox = new ReleaseSandbox();
        var runner = new FakeProcessRunner
        {
            Outcome = ProcessOutcome.Failed,
            ExitCode = 4,
            FailureHint = "Replacement failed."
        };
        var preview = sandbox.CreatePreview();

        var result = await sandbox.CreateService(runner).ApplyConfirmedAsync(preview, confirmed: true);

        Assert.False(result.Succeeded);
        Assert.Equal("Replacement failed.", result.Message);
        Assert.Equal(4, result.ProcessResult!.ExitCode);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task ApplyConfirmedAsync_PreCancelledOperationDoesNotStartProcess()
    {
        using var sandbox = new ReleaseSandbox();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new FakeProcessRunner();
        var preview = sandbox.CreatePreview();

        var result = await sandbox.CreateService(runner).ApplyConfirmedAsync(
            preview,
            confirmed: true,
            cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(ProcessOutcome.Canceled, result.ProcessResult!.Outcome);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task ApplyConfirmedAsync_RefusesMissingToolingWithoutStartingProcess()
    {
        using var sandbox = new ReleaseSandbox();
        var runner = new FakeProcessRunner();
        var preview = sandbox.CreatePreview();
        var service = new ReleaseVersionApplyService(
            runner,
            Path.Combine(sandbox.Root, "missing.ps1"),
            sandbox.ToolingDirectory);

        var result = await service.ApplyConfirmedAsync(preview, confirmed: true);

        Assert.False(result.Succeeded);
        Assert.Contains("script is unavailable", result.Message);
        Assert.Empty(runner.Requests);
    }

    private sealed class ReleaseSandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "devdesk-release-apply-" + Guid.NewGuid().ToString("N"));
        public string ToolingDirectory => Path.Combine(Root, "DevDesk tooling");
        public string ScriptPath => Path.Combine(ToolingDirectory, "bump-version.ps1");
        private string ProjectFile => Path.Combine(Root, "Project with spaces", "App.csproj");

        public ReleaseSandbox()
        {
            Directory.CreateDirectory(ToolingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectFile)!);
            System.IO.File.WriteAllText(ScriptPath, "# test placeholder");
            System.IO.File.WriteAllText(ProjectFile, ProjectXml("0.6.0", "0.6.0-beta"));
        }

        public ReleaseVersionPreview CreatePreview()
        {
            var suggestion = new ReleaseVersionSuggestion(
                "0.6.0",
                "0.6.1",
                ReleaseBumpKind.Patch,
                ReleaseChannel.Beta,
                "v0.6.1-beta");
            Assert.True(ReleaseVersionPreviewBuilder.TryCreate(
                ProjectFile,
                suggestion,
                out var preview,
                out var error), error);
            return preview!;
        }

        public ReleaseVersionApplyService CreateService(FakeProcessRunner runner) =>
            new(runner, ScriptPath, ToolingDirectory);

        public static string ProjectXml(string version, string informationalVersion) =>
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup>\n" +
            $"    <Version>{version}</Version>\n" +
            $"    <AssemblyVersion>{version}.0</AssemblyVersion>\n" +
            $"    <FileVersion>{version}.0</FileVersion>\n" +
            $"    <InformationalVersion>{informationalVersion}</InformationalVersion>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n";

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
