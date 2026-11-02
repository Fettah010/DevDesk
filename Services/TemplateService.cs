using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DevDesk.Models;

namespace DevDesk.Services;

/// <summary>
/// Template service backed by the shared catalog scan. Templates are discovered
/// by <see cref="ScanEngine"/> alongside projects and cached in the session's
/// single snapshot; this service never re-scans and never mutates the template.
/// </summary>
public sealed class TemplateService : ITemplateService
{
    private readonly ProjectCatalog _catalog;
    private readonly TemplateCreationService _creation;

    public TemplateService(ProjectCatalog catalog, TemplateCreationService? creation = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _creation = creation ?? new TemplateCreationService();
    }

    public Task<IReadOnlyList<TemplateDescriptor>> ListAvailableTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = _catalog.Snapshot;
        return Task.FromResult<IReadOnlyList<TemplateDescriptor>>(snapshot.Templates);
    }

    public async Task<WizardResult> RunWizardAsync(
        CreateRequest request,
        IProgress<TemplateCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _creation.CreateAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);
    }
}
