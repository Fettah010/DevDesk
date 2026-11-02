using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;
using GitInitResult = DevDesk.Models.GitResult;

namespace DevDesk.Services;

/// <summary>
/// Template discovery and scaffolding. The template is read-only — this service
/// never writes inside the template folder.
/// </summary>
public interface ITemplateService
{
    /// <summary>All templates discovered by the scan, in stable discovery order.</summary>
    Task<IReadOnlyList<TemplateDescriptor>> ListAvailableTemplatesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the full create chain: validate, copy with tokens, optional git init
    /// + first commit, smoke build. Returns a result the wizard can render.
    /// </summary>
    Task<WizardResult> RunWizardAsync(
        CreateRequest request,
        IProgress<TemplateCopyProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Everything the wizard needs to create one app.</summary>
public sealed record CreateRequest(
    TemplateDescriptor Template,
    WizardInputs Inputs);

/// <summary>User-entered values from the name/location/options step.</summary>
public sealed record WizardInputs(
    string Name,
    string Location,
    WizardOptions Options);

/// <summary>Toggleable options for the new app.</summary>
public sealed record WizardOptions(
    bool InitGit = true,
    bool CreateGitHubRepo = false,
    string AuthorName = "DevDesk",
    string PackAuthors = "DevDesk",
    string RepoUrl = "",
    string Version = "0.1.0");

/// <summary>Outcome of a create chain.</summary>
public sealed record WizardResult(
    string TargetPath,
    BuildResult BuildInfo,
    GitInitResult? GitInfo = null);

/// <summary>Progress reported per file while copying.</summary>
public sealed record TemplateCopyProgress(string FileName, int FilesDone, int FilesTotal);
