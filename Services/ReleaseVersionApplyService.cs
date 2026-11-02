using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DevDesk.Services;

public sealed record ReleaseVersionApplyResult(
    bool Succeeded,
    string Message,
    ProcessResult? ProcessResult = null);

public interface IReleaseVersionApplyService
{
    Task<ReleaseVersionApplyResult> ApplyConfirmedAsync(
        ReleaseVersionPreview preview,
        bool confirmed,
        CancellationToken cancellationToken = default);
}

public sealed class ReleaseVersionApplyService : IReleaseVersionApplyService
{
    private readonly IProcessRunner _processRunner;
    private readonly string _scriptPath;
    private readonly string _toolingDirectory;
    private readonly string _shell;

    public ReleaseVersionApplyService(
        IProcessRunner processRunner,
        string scriptPath,
        string toolingDirectory,
        string shell = "powershell.exe")
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _scriptPath = Path.GetFullPath(scriptPath);
        _toolingDirectory = Path.GetFullPath(toolingDirectory);
        _shell = shell;
    }

    public async Task<ReleaseVersionApplyResult> ApplyConfirmedAsync(
        ReleaseVersionPreview preview,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        if (!confirmed)
        {
            return new ReleaseVersionApplyResult(false, "Version update was not confirmed.");
        }

        if (!File.Exists(_scriptPath))
        {
            return new ReleaseVersionApplyResult(false, "The version update script is unavailable.");
        }

        if (!Directory.Exists(_toolingDirectory))
        {
            return new ReleaseVersionApplyResult(false, "The release tooling folder is unavailable.");
        }

        var channel = preview.Channel switch
        {
            ReleaseChannels.Stable => ReleaseChannel.Stable,
            ReleaseChannels.Beta => ReleaseChannel.Beta,
            ReleaseChannels.Dev => ReleaseChannel.Dev,
            _ => (ReleaseChannel?)null
        };
        if (channel is null)
        {
            return new ReleaseVersionApplyResult(false, "The release preview has an unknown channel.");
        }

        var expectedSuggestion = new ReleaseVersionSuggestion(
            preview.BaselineVersion,
            preview.ProposedVersion,
            ReleaseBumpKind.Patch,
            channel.Value,
            preview.Tag);

        if (!ReleaseVersionPreviewBuilder.TryCreate(
                preview.ProjectFile,
                expectedSuggestion,
                out var currentPreview,
                out var validationError))
        {
            return new ReleaseVersionApplyResult(
                false,
                $"The project changed after this preview was created or is no longer valid: {validationError}");
        }

        if (currentPreview is null
            || currentPreview.CurrentVersion != preview.CurrentVersion
            || currentPreview.BaselineVersion != preview.BaselineVersion
            || currentPreview.ProposedVersion != preview.ProposedVersion
            || currentPreview.Channel != preview.Channel
            || currentPreview.Tag != preview.Tag
            || !currentPreview.Changes.SequenceEqual(preview.Changes))
        {
            return new ReleaseVersionApplyResult(
                false,
                "The project changed after this preview was created. Review a refreshed preview before applying.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ReleaseVersionApplyResult(
                false,
                "Version update was cancelled.",
                new ProcessResult(ProcessOutcome.Canceled, -1, string.Empty, TimeSpan.Zero, "Cancelled."));
        }

        var suffix = channel.Value == ReleaseChannel.Stable
            ? "none"
            : ReleaseChannels.ToName(channel.Value);
        var request = new ProcessRequest(
            _shell,
            [
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                _scriptPath,
                "-Version",
                preview.ProposedVersion,
                "-Suffix",
                suffix,
                "-Project",
                preview.ProjectFile
            ],
            _toolingDirectory);

        var processResult = await _processRunner.RunAsync(request, output: null, cancellationToken)
            .ConfigureAwait(false);
        if (!processResult.Succeeded)
        {
            return new ReleaseVersionApplyResult(
                false,
                processResult.FailureHint ?? "The version update script did not complete successfully.",
                processResult);
        }

        if (!ReleaseVersionPreviewBuilder.TryVerifyApplied(
                preview.ProjectFile,
                preview.ProposedVersion,
                preview.Channel,
                out var postApplyError))
        {
            return new ReleaseVersionApplyResult(
                false,
                $"The version script completed but the project could not be verified: {postApplyError}",
                processResult);
        }

        return new ReleaseVersionApplyResult(true, "The project version fields were updated.", processResult);
    }
}
