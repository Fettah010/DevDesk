using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using DevDesk.Models;
using DevDesk.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace DevDesk.Pages;

public sealed partial class GitHubPage : Page
{
    private readonly ObservableCollection<ProjectModel> _repositories = new();
    private readonly GitService _git = new();
    private readonly string? _releaseToolingDirectory;
    private readonly string? _releaseVersionScript;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _releaseCancellation;
    private string? _releaseRunUrl;
    private string? _releaseRunRepositoryPath;
    private bool _isPageLoaded;
    private bool CanExecuteRelease =>
        RepositoryPicker.SelectedItem is not null
        && _releaseCancellation is null
        && _releaseToolingDirectory is not null
        && _releaseVersionScript is not null
        && File.Exists(_releaseVersionScript);
    private bool _initializing;

    public GitHubPage()
    {
        InitializeComponent();
        RepositoryPicker.ItemsSource = _repositories;
        _releaseToolingDirectory = SelfLocator.FindSourceRepo(AppContext.BaseDirectory);
        _releaseVersionScript = _releaseToolingDirectory is null
            ? null
            : Path.Combine(_releaseToolingDirectory, "Scripts", "bump-version.ps1");
        ReleaseButton.IsEnabled = false;
        if (_releaseToolingDirectory is null || _releaseVersionScript is null || !File.Exists(_releaseVersionScript))
        {
            PageDescription.Text = "GitHub data is read-only. Release execution requires the DevDesk source checkout.";
            StateInfoBar.Message = "Release execution is unavailable in this installed build; no release files can be changed.";
            StateInfoBar.Severity = InfoBarSeverity.Informational;
            StateInfoBar.IsOpen = true;
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _isPageLoaded = false;
        var cancellation = _loadCancellation;
        _loadCancellation = null;
        cancellation?.Cancel();
        _releaseCancellation?.Cancel();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _isPageLoaded = true;
        _loadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _initializing = true;
        RefreshButton.IsEnabled = false;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;

        try
        {
            var snapshot = await AppServices.Catalog.GetOrRefreshAsync(cancellation.Token);
            var previousPath = (RepositoryPicker.SelectedItem as ProjectModel)?.Path;
            _repositories.Clear();
            if (snapshot.Self is not null)
            {
                _repositories.Add(snapshot.Self);
            }

            foreach (var project in snapshot.Projects)
            {
                _repositories.Add(project);
            }

            if (_repositories.Count == 0)
            {
                RepositoryTitle.Text = "No local repositories found";
                RepositorySummary.Text = "Add a GitHub repository to a configured scan root to read its GitHub data.";
                AccountText.Text = "Public access";
                StateInfoBar.IsOpen = false;
                return;
            }

            var selectedIndex = previousPath is null
                ? 0
                : Math.Max(0, _repositories.ToList().FindIndex(project =>
                    string.Equals(project.Path, previousPath, StringComparison.OrdinalIgnoreCase)));
            RepositoryPicker.SelectedIndex = selectedIndex;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "GitHub page could not load the local repository list");
            ShowState("The local repository list could not be loaded.", InfoBarSeverity.Error);
        }
        finally
        {
            _initializing = false;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = RepositoryPicker.SelectedItem is not null;
            ReleaseButton.IsEnabled = CanExecuteRelease;
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                _loadCancellation = null;
            }

            cancellation.Dispose();
        }

        if (RepositoryPicker.SelectedItem is ProjectModel selected)
        {
            await LoadSelectedRepositoryAsync(selected, forceRefresh: false);
        }
    }

    private async void RepositoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || RepositoryPicker.SelectedItem is not ProjectModel selected)
        {
            return;
        }

        if (!string.Equals(_releaseRunRepositoryPath, selected.Path, StringComparison.OrdinalIgnoreCase))
        {
            _releaseRunUrl = null;
            _releaseRunRepositoryPath = null;
            ReleaseRunLink.Visibility = Visibility.Collapsed;
        }

        await LoadSelectedRepositoryAsync(selected, forceRefresh: false);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (RepositoryPicker.SelectedItem is ProjectModel selected)
        {
            await LoadSelectedRepositoryAsync(selected, forceRefresh: true);
        }
    }

    private async void ReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (RepositoryPicker.SelectedItem is not ProjectModel project
            || _releaseCancellation is not null
            || !CanExecuteRelease)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _releaseCancellation = cancellation;
        ReleaseButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        RepositoryPicker.IsEnabled = false;
        ReleaseRunLink.Visibility = Visibility.Collapsed;
        _releaseRunUrl = null;
        _releaseRunRepositoryPath = null;
        try
        {
            var token = cancellation.Token;
            var repositoryPath = Path.GetFullPath(project.Path);
            var status = await _git.GetStatusAsync(repositoryPath, token);
            if (!status.Operation.Succeeded || status.Value is null)
            {
                ShowState("The repository status could not be verified; release preparation stopped.", InfoBarSeverity.Error);
                return;
            }

            if (status.Value.IsBareRepository || status.Value.Files.Count != 0)
            {
                ShowState("Release preparation requires a clean working tree. Review or commit the existing changes first.", InfoBarSeverity.Warning);
                return;
            }

            var remotes = await _git.GetRemotesAsync(repositoryPath, token);
            if (!remotes.Operation.Succeeded || remotes.Value is null)
            {
                ShowState("Git remotes could not be read; release preparation stopped.", InfoBarSeverity.Error);
                return;
            }

            var fetchRemote = remotes.Value.FirstOrDefault(remote =>
                    string.Equals(remote.Name, "origin", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(remote.Direction, "fetch", StringComparison.OrdinalIgnoreCase))
                ?? remotes.Value.FirstOrDefault(remote =>
                    string.Equals(remote.Direction, "fetch", StringComparison.OrdinalIgnoreCase));
            var identity = GitHubRepositoryIdentityParser.Parse(fetchRemote?.Url);
            if (identity is not { SupportsPublicApi: true })
            {
                ShowState("Release preparation requires a supported github.com fetch remote.", InfoBarSeverity.Warning);
                return;
            }

            var pushRemote = remotes.Value
                .Where(remote => string.Equals(remote.Direction, "push", StringComparison.OrdinalIgnoreCase))
                .OrderBy(remote => string.Equals(remote.Name, "origin", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault(remote => ReleasePlanBuilder.IsSelectedPushRemote(remote, identity));
            if (pushRemote is null)
            {
                ShowState("No configured push remote points to the selected GitHub repository.", InfoBarSeverity.Warning);
                return;
            }

            var tags = await _git.GetTagsAsync(repositoryPath, token);
            if (!tags.Operation.Succeeded || tags.Value is null)
            {
                ShowState("Local release tags could not be read; release preparation stopped.", InfoBarSeverity.Error);
                return;
            }

            var projectFiles = FindVersionProjectFiles(repositoryPath);
            if (projectFiles.Count == 0)
            {
                ShowState("No MSBuild project files were found in this repository.", InfoBarSeverity.Warning);
                return;
            }

            await ShowReleaseFlowAsync(
                project,
                repositoryPath,
                identity,
                pushRemote,
                tags.Value.Select(tag => tag.Name).ToArray(),
                projectFiles,
                token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "Release preparation failed for {Repository}", project.Name);
            ShowState("Release preparation could not be completed. No release push was attempted.", InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_releaseCancellation, cancellation))
            {
                _releaseCancellation = null;
            }
            ReleaseButton.IsEnabled = CanExecuteRelease;
            RefreshButton.IsEnabled = RepositoryPicker.SelectedItem is not null;
            RepositoryPicker.IsEnabled = true;
            cancellation.Dispose();
        }
    }

    private async Task ShowReleaseFlowAsync(
        ProjectModel project,
        string repositoryPath,
        GitHubRepositoryIdentity identity,
        GitRemoteInfo pushRemote,
        string[] tags,
        IReadOnlyList<ReleaseProjectFileOption> projectFiles,
        CancellationToken cancellationToken)
    {
        var projectPicker = new ComboBox
        {
            ItemsSource = projectFiles,
            DisplayMemberPath = nameof(ReleaseProjectFileOption.Name),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(projectPicker, "Select the project file whose version will be released");
        projectPicker.SelectedIndex = 0;

        var channelPicker = new ComboBox
        {
            ItemsSource = new[]
            {
                new ReleaseChannelOption("Stable", DevDesk.Services.ReleaseChannel.Stable),
                new ReleaseChannelOption("Beta", DevDesk.Services.ReleaseChannel.Beta),
                new ReleaseChannelOption("Dev", DevDesk.Services.ReleaseChannel.Dev)
            },
            DisplayMemberPath = nameof(ReleaseChannelOption.Name),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(channelPicker, "Release channel");
        channelPicker.SelectedItem = channelPicker.Items.Cast<ReleaseChannelOption>()
            .First(option => option.Value == DevDesk.Services.ReleaseChannel.Beta);

        var bumpPicker = new ComboBox
        {
            ItemsSource = new[]
            {
                new ReleaseBumpOption("Patch (default)", ReleaseBumpKind.Patch),
                new ReleaseBumpOption("Minor", ReleaseBumpKind.Minor),
                new ReleaseBumpOption("Major", ReleaseBumpKind.Major)
            },
            DisplayMemberPath = nameof(ReleaseBumpOption.Name),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(bumpPicker, "Version increment");
        bumpPicker.SelectedIndex = 0;

        var previewText = new TextBlock
        {
            Text = "Choose a project, channel and increment, then generate a read-only preview.",
            TextWrapping = TextWrapping.Wrap
        };
        var notesEditor = new TextBox
        {
            Header = "Release notes · ReleaseNotes.md",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 160,
            MaxHeight = 260,
            IsSpellCheckEnabled = true
        };
        AutomationProperties.SetName(notesEditor, "Edit release notes before saving");
        var generateButton = new Button
        {
            Content = "Generate preview and notes",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(generateButton, "Generate a read-only release preview and local notes draft");
        ReleaseVersionPreview? currentPreview = null;
        string? previewProjectFile = null;
        string? previewError = null;
        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(new TextBlock
        {
            Text = $"{identity.Owner}/{identity.Repository} · push remote {pushRemote.Name} · {project.Path}",
            TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(new TextBlock { Text = "Project file", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        form.Children.Add(projectPicker);
        form.Children.Add(new TextBlock { Text = "Channel", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        form.Children.Add(channelPicker);
        form.Children.Add(new TextBlock { Text = "Version increment", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        form.Children.Add(bumpPicker);
        form.Children.Add(generateButton);
        form.Children.Add(previewText);
        form.Children.Add(notesEditor);

        var dialog = new ContentDialog
        {
            Title = "Prepare release",
            Content = new ScrollViewer
            {
                Content = form,
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            PrimaryButtonText = "Review release",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        AutomationProperties.SetName(dialog, "Release preview and notes");

        generateButton.Click += async (_, _) =>
        {
            currentPreview = null;
            previewProjectFile = null;
            previewError = null;
            var selectedFile = projectPicker.SelectedItem as ReleaseProjectFileOption;
            var selectedChannel = (channelPicker.SelectedItem as ReleaseChannelOption)?.Value;
            var selectedBump = (bumpPicker.SelectedItem as ReleaseBumpOption)?.Value;
            if (selectedFile is null || selectedChannel is null || selectedBump is null)
            {
                previewText.Text = "Select a project file, channel and version increment.";
                return;
            }

            try
            {
                var currentVersion = ReadProjectVersion(selectedFile.Path);
                if (!ReleaseVersionSuggester.TrySuggestFromTags(
                        tags,
                        currentVersion,
                        selectedBump.Value,
                        selectedChannel.Value,
                        out var suggestion,
                        out var suggestionError)
                    || suggestion is null)
                {
                    previewText.Text = suggestionError;
                    return;
                }

                if (!ReleaseVersionPreviewBuilder.TryCreate(
                        selectedFile.Path,
                        suggestion,
                        out currentPreview,
                        out var versionError)
                    || currentPreview is null)
                {
                    previewText.Text = versionError;
                    return;
                }

                var previousTag = FindLatestChannelTag(tags, selectedChannel.Value);
                var notesDraft = await new ReleaseNotesDraftService(_git)
                    .CreateAsync(repositoryPath, previousTag, cancellationToken);
                if (!notesDraft.Succeeded || notesDraft.Draft is null)
                {
                    currentPreview = null;
                    previewText.Text = notesDraft.Error;
                    return;
                }

                notesEditor.Text = notesDraft.Draft.Markdown;
                if (notesDraft.Draft.IsTruncated)
                {
                    previewText.Text = "The notes draft is limited to the latest 500 commits. Review and edit it before continuing.";
                }
                else
                {
                    previewText.Text = "Review the proposed version and edit the notes before continuing.";
                }

                previewText.Text += Environment.NewLine + Environment.NewLine
                    + $"Current: {currentPreview.CurrentVersion}{Environment.NewLine}"
                    + $"Baseline: {currentPreview.BaselineVersion}{Environment.NewLine}"
                    + $"Proposed: {currentPreview.ProposedVersion}{Environment.NewLine}"
                    + $"Channel: {currentPreview.Channel}{Environment.NewLine}"
                    + $"Tag: {currentPreview.Tag}{Environment.NewLine}{Environment.NewLine}"
                    + currentPreview.Diff;
                previewProjectFile = selectedFile.Path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                currentPreview = null;
                previewError = "The selected project version could not be read.";
                LoggingService.Log.Warning(ex, "Could not read release project version at {ProjectFile}", selectedFile.Path);
                previewText.Text = previewError;
            }
            catch (Exception ex)
            {
                currentPreview = null;
                previewText.Text = "The release preview or notes draft could not be created.";
                LoggingService.Log.Error(ex, "Could not create release preview for {Repository}", project.Name);
            }
        };

        projectPicker.SelectionChanged += (_, _) =>
        {
            currentPreview = null;
            previewText.Text = "Project selection changed. Generate a fresh preview.";
        };
        channelPicker.SelectionChanged += (_, _) =>
        {
            currentPreview = null;
            previewText.Text = "Channel selection changed. Generate a fresh preview.";
        };
        bumpPicker.SelectionChanged += (_, _) =>
        {
            currentPreview = null;
            previewText.Text = "Version increment changed. Generate a fresh preview.";
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (currentPreview is null || previewProjectFile is null)
            {
                args.Cancel = true;
                previewText.Text = previewError ?? "Generate a valid version and notes preview before reviewing the release.";
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || currentPreview is null
            || previewProjectFile is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(notesEditor.Text))
        {
            ShowState("Release notes are empty. Add notes and generate the preview again before saving.", InfoBarSeverity.Warning);
            return;
        }

        var notesPath = ReleaseNotesSaveService.DefaultTargetPath(repositoryPath);
        if (!ReleasePlanBuilder.TryCreate(
                repositoryPath,
                identity,
                pushRemote,
                currentPreview,
                notesPath,
                notesEditor.Text,
                $"Release {currentPreview.Tag}",
                out var plan,
                out var planError)
            || plan is null)
        {
            ShowState(planError, InfoBarSeverity.Error);
            return;
        }

        if (_releaseToolingDirectory is null
            || _releaseVersionScript is null
            || !File.Exists(_releaseVersionScript))
        {
            ShowState("The version update script is unavailable; no release files were changed.", InfoBarSeverity.Error);
            return;
        }

        await ConfirmAndExecuteReleaseAsync(project, plan, cancellationToken);
    }

    private async Task ConfirmAndExecuteReleaseAsync(
        ProjectModel project,
        ReleasePlan plan,
        CancellationToken cancellationToken)
    {
        var notesAlreadyExist = File.Exists(plan.ReleaseNotesPath);
        if (notesAlreadyExist)
        {
            var overwriteConfirmation = new ContentDialog
            {
                Title = "Replace existing release notes?",
                Content = $"The release will replace {Path.GetRelativePath(plan.RepositoryPath, plan.ReleaseNotesPath)}. "
                    + "Its current contents will be replaced by the notes you reviewed.",
                PrimaryButtonText = "Replace existing notes",
                CloseButtonText = "Keep existing file",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            AutomationProperties.SetName(
                overwriteConfirmation,
                $"Confirm replacement of {Path.GetRelativePath(plan.RepositoryPath, plan.ReleaseNotesPath)}");
            if (await overwriteConfirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        var confirmation = new ContentDialog
        {
            Title = "Review and confirm local release changes",
            Content = $"Repository: {plan.Repository.Owner}/{plan.Repository.Repository}\n"
                + $"Project: {Path.GetRelativePath(plan.RepositoryPath, plan.ProjectFile)}\n"
                + $"Version: {plan.ProposedVersion} ({plan.Channel})\n"
                + $"Tag: {plan.Tag}\n"
                + $"Notes: {Path.GetRelativePath(plan.RepositoryPath, plan.ReleaseNotesPath)}"
                + (notesAlreadyExist ? " (existing file will be replaced)" : string.Empty) + "\n"
                + $"Commit: {plan.CommitMessage}\n\n"
                + "Continuing saves these notes, updates the four version fields, creates a local commit and tag. "
                + "Nothing is pushed until you separately confirm the exact remote push.",
            PrimaryButtonText = "Save notes, commit and tag",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        AutomationProperties.SetName(confirmation, $"Confirm local release {plan.Tag}");
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetLoading(true);
        try
        {
            var sourceRoot = _releaseToolingDirectory;
            var versionScript = _releaseVersionScript;
            if (sourceRoot is null || versionScript is null || !File.Exists(versionScript))
            {
                ShowState("The version update script is unavailable; no release files were changed.", InfoBarSeverity.Error);
                return;
            }

            var applyService = new ReleaseVersionApplyService(
                new ProcessRunner(),
                versionScript,
                sourceRoot);
            var gitExecution = new ReleaseGitExecutionService(_git);
            var coordinator = new ReleaseOperationCoordinator(
                AppServices.Activity,
                new ReleaseNotesSaveService(),
                applyService,
                gitExecution);
            var localOperation = await coordinator
                .SubmitLocalRelease(project.Name, plan, confirmed: true, overwriteNotesConfirmed: notesAlreadyExist)
                .Completion;
            if (localOperation.Status != OperationStatus.Succeeded)
            {
                ShowState(
                    localOperation.FirstError ?? localOperation.Hint ?? "The local release operation did not complete.",
                    localOperation.Status == OperationStatus.Canceled ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
                return;
            }

            if (!_isPageLoaded || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var pushConfirmation = new ContentDialog
            {
                Title = "Push release tag and start publication?",
                Content = $"Push {plan.Tag} to remote {plan.RemoteName} ({plan.RemoteUrl})?\n\n"
                    + "This triggers the repository's release workflow and may publish the release. "
                    + "DevDesk will not force-push, move or delete the tag.",
                PrimaryButtonText = $"Push {plan.Tag}",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            AutomationProperties.SetName(
                pushConfirmation,
                $"Confirm push of release tag {plan.Tag} to {plan.RemoteName}");
            if (await pushConfirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                ShowState($"Local commit and tag {plan.Tag} are ready. The tag was not pushed.", InfoBarSeverity.Informational);
                return;
            }

            var pushOperation = await coordinator
                .SubmitTagPush(project.Name, plan, confirmed: true)
                .Completion;
            if (pushOperation.Status != OperationStatus.Succeeded)
            {
                ShowState(
                    pushOperation.FirstError ?? pushOperation.Hint ?? "The release tag push failed.",
                    InfoBarSeverity.Error);
                return;
            }

            if (localOperation.OutputPath is not { Length: 40 or 64 } commitSha
                || !commitSha.All(Uri.IsHexDigit))
            {
                ShowState($"Tag {plan.Tag} was pushed, but the commit SHA could not be verified; automatic monitoring stopped.", InfoBarSeverity.Warning);
                return;
            }

            ReleaseRunLink.Visibility = Visibility.Collapsed;
            _releaseRunRepositoryPath = plan.RepositoryPath;
            RateLimitText.Text = $"Monitoring release workflow for {plan.Tag}…";
            var monitor = new ReleaseWorkflowMonitor(AppServices.GitHub);
            var progress = new Progress<ReleaseWorkflowMonitorUpdate>(update =>
            {
                RateLimitText.Text = $"{update.State} · {update.Elapsed.TotalSeconds:0}s";
                if (update.RunUrl is not null)
                {
                    _releaseRunUrl = update.RunUrl;
                    ReleaseRunLink.Visibility = Visibility.Visible;
                }
                ShowState(update.Message ?? $"Release workflow: {update.State}.", update.State switch
                {
                    ReleaseWorkflowMonitorState.Succeeded => InfoBarSeverity.Success,
                    ReleaseWorkflowMonitorState.Failed or ReleaseWorkflowMonitorState.Error => InfoBarSeverity.Error,
                    ReleaseWorkflowMonitorState.Cancelled or ReleaseWorkflowMonitorState.TimedOut
                        or ReleaseWorkflowMonitorState.MonitoringStopped => InfoBarSeverity.Warning,
                    _ => InfoBarSeverity.Informational
                });
            });

            var monitorResult = await monitor.MonitorAsync(
                plan.Repository,
                plan.Tag,
                commitSha,
                progress,
                cancellationToken);
            if (monitorResult.State == ReleaseWorkflowMonitorState.Succeeded
                && ReferenceEquals(RepositoryPicker.SelectedItem, project))
            {
                await LoadSelectedRepositoryAsync(project, true, cancellationToken);
            }
        }
        finally
        {
            SetLoading(false);
        }
    }

    private async void ReleaseRunLink_Click(object sender, RoutedEventArgs e) =>
        await OpenLinkAsync(_releaseRunUrl);

    private static List<ReleaseProjectFileOption> FindVersionProjectFiles(string repositoryPath)
    {
        var results = new List<ReleaseProjectFileOption>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((repositoryPath, 0));
        var ignored = new[] { ".git", ".vs", "bin", "obj", "packages", "node_modules", "Releases", "Logs" };
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();
            try
            {
                foreach (var file in Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    var relative = Path.GetRelativePath(repositoryPath, file);
                    results.Add(new ReleaseProjectFileOption(relative, file));
                }

                if (depth >= 3)
                {
                    continue;
                }

                foreach (var child in Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(child);
                    if (string.IsNullOrWhiteSpace(name)
                        || name.StartsWith(".", StringComparison.Ordinal)
                        || ignored.Contains(name, StringComparer.OrdinalIgnoreCase)
                        || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    pending.Enqueue((child, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.Log.Warning(ex, "Could not inspect release project directory {Directory}", directory);
            }
        }

        return results.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ReadProjectVersion(string projectFile)
    {
        var document = XDocument.Load(projectFile);
        var versions = document.Root?.Descendants()
            .Where(element => element.Name.LocalName == "Version")
            .ToArray();
        return versions is { Length: 1 } ? versions[0].Value.Trim() : string.Empty;
    }

    private static string? FindLatestChannelTag(IEnumerable<string> tags, ReleaseChannel channel)
    {
        string? latestTag = null;
        string? latestVersion = null;
        foreach (var tag in tags)
        {
            if (!ReleaseChannels.TryParseTagTyped(tag, out var version, out var tagChannel)
                || tagChannel != channel
                || !ReleaseVersionSuggester.IsValidNumericVersion(version))
            {
                continue;
            }

            var canonicalTag = channel == ReleaseChannel.Stable
                ? $"v{version}"
                : $"v{version}-{ReleaseChannels.ToName(channel)}";
            if (!string.Equals(tag, canonicalTag, StringComparison.Ordinal))
            {
                continue;
            }

            if (latestVersion is null
                || ReleaseVersionSuggester.TryCompareNumericVersions(version, latestVersion, out var comparison)
                && comparison > 0)
            {
                latestTag = tag;
                latestVersion = version;
            }
        }

        return latestTag;
    }

    private async Task LoadSelectedRepositoryAsync(
        ProjectModel project,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        var previous = _loadCancellation;
        _loadCancellation = null;
        previous?.Cancel();
        var current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadCancellation = current;
        var token = current.Token;
        SetLoading(true);
        ClearRows();
        StateInfoBar.IsOpen = false;
        RateLimitText.Text = "Reading GitHub data on demand…";

        try
        {
            RepositoryTitle.Text = project.Name;
            RepositorySummary.Text = project.Path;

            var remotes = await _git.GetRemotesAsync(project.Path, token);
            if (!remotes.Operation.Succeeded || remotes.Value is null)
            {
                ShowState("Git remotes could not be read for this repository.", InfoBarSeverity.Error);
                return;
            }

            var remote = remotes.Value.FirstOrDefault(item =>
                    string.Equals(item.Name, "origin", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Direction, "fetch", StringComparison.OrdinalIgnoreCase))
                ?? remotes.Value.FirstOrDefault(item =>
                    string.Equals(item.Direction, "fetch", StringComparison.OrdinalIgnoreCase));
            if (remote is null)
            {
                RepositoryTitle.Text = project.Name;
                RepositorySummary.Text = "No fetch remote is configured.";
                ShowState("This local repository has no GitHub remote to enrich.", InfoBarSeverity.Informational);
                return;
            }

            var identity = GitHubRepositoryIdentityParser.Parse(remote.Url);
            if (identity is null)
            {
                RepositorySummary.Text = "The configured fetch remote is not a supported repository URL.";
                ShowState("No GitHub data is available for this remote.", InfoBarSeverity.Informational);
                return;
            }

            RepositoryTitle.Text = $"{identity.Owner}/{identity.Repository}";
            if (!identity.SupportsPublicApi)
            {
                RepositorySummary.Text = $"{identity.Host} · local repository: {project.Path}";
                ShowState("GitHub API enrichment for enterprise or other hosts is not supported in v0.5.0.", InfoBarSeverity.Informational);
                return;
            }

            var service = AppServices.GitHub;
            ShowCachedPreview(service, identity, project.Path, forceRefresh);
            await LoadAccountAsync(token);
            token.ThrowIfCancellationRequested();

            var repository = await service.GetRepositoryAsync(identity, forceRefresh, token);
            UpdateRepositorySummary(repository, project.Path);
            token.ThrowIfCancellationRequested();

            var releases = await service.GetReleasesAsync(identity, forceRefresh, token);
            if (releases.Data is not null)
            {
                ReleasesList.ItemsSource = releases.Data
                    .OrderBy(release => ReleaseChannelOrder(GetReleaseChannel(release.TagName)))
                    .Select(release => new ReleaseRow(
                        release.Name ?? release.TagName,
                        GetReleaseChannel(release.TagName),
                        FormatDate(release.PublishedAt),
                        release.HtmlUrl))
                    .ToArray();
                ReleasesEmpty.Visibility = releases.Data.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                ReleasesList.Visibility = releases.Data.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }

            token.ThrowIfCancellationRequested();
            var runs = await service.GetWorkflowRunsAsync(identity, forceRefresh, token);
            if (runs.Data?.Runs is { } workflowRuns)
            {
                RunsList.ItemsSource = workflowRuns
                    .Select(run => new RunRow(
                        run.Name ?? $"Workflow run #{run.RunNumber}",
                        $"{run.Status ?? "unknown"}{(string.IsNullOrWhiteSpace(run.Conclusion) ? string.Empty : $" · {run.Conclusion}")} · {run.Branch ?? "branch unavailable"}",
                        FormatDate(run.StartedAt ?? run.CreatedAt),
                        run.HtmlUrl))
                    .ToArray();
                RunsEmpty.Visibility = workflowRuns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                RunsList.Visibility = workflowRuns.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }

            token.ThrowIfCancellationRequested();
            var pulls = await service.GetOpenPullRequestSummariesAsync(identity, forceRefresh, token);
            if (pulls.Data is not null)
            {
                PullRequestsList.ItemsSource = pulls.Data
                    .Select(summary => new PullRequestRow(
                        $"#{summary.PullRequest.Number} · {summary.PullRequest.Title}",
                        $"{(summary.PullRequest.Draft ? "Draft" : "Open")} · {summary.ChecksSummary}",
                        FormatDate(summary.PullRequest.UpdatedAt),
                        summary.PullRequest.HtmlUrl))
                    .ToArray();
                PullRequestsEmpty.Visibility = pulls.Data.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                PullRequestsList.Visibility = pulls.Data.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }

            var rateLimit = pulls.RateLimit ?? runs.RateLimit ?? releases.RateLimit ?? repository.RateLimit;
            RateLimitText.Text = FormatRateLimit(rateLimit);
            ShowDataState(repository, releases, runs, pulls);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "GitHub data load failed for {Repository}", project.Name);
            ShowState("GitHub data could not be loaded. Cached data, if available, remains on disk.", InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, current))
            {
                _loadCancellation = null;
                SetLoading(false);
            }

            current.Dispose();
        }
    }

    private async Task LoadAccountAsync(CancellationToken cancellationToken)
    {
        var service = AppServices.GitHub;
        if (!service.HasToken)
        {
            AccountText.Text = "Public access · not signed in";
            return;
        }

        AccountText.Text = "Checking account…";
        var user = await service.GetCurrentUserAsync(cancellationToken);
        if (user.Succeeded && user.Data is not null)
        {
            AccountText.Text = $"Signed in as {user.Data.Login}";
        }
        else
        {
            AccountText.Text = user.ErrorKind == GitHubErrorKind.Authentication
                ? "Saved token rejected"
                : "Signed in · repository access varies";
            if (user.ErrorKind == GitHubErrorKind.Authentication)
            {
                ShowState(user.Message ?? "The saved token was rejected. Update it in Settings.", InfoBarSeverity.Warning);
            }
        }
    }

    private void UpdateRepositorySummary(GitHubDataResult<GitHubRepository> result, string localPath)
    {
        if (result.Data is { } repository)
        {
            RepositorySummary.Text = $"{repository.FullName} · {(repository.IsPrivate ? "Private" : "Public")} · {localPath}";
        }
        else
        {
            RepositorySummary.Text = localPath;
        }
    }

    private void ShowCachedPreview(
        GitHubService service,
        GitHubRepositoryIdentity identity,
        string localPath,
        bool forceRefresh)
    {
        var repository = service.GetCachedRepository(identity);
        var releases = service.GetCachedReleases(identity);
        var runs = service.GetCachedWorkflowRuns(identity);
        var pulls = service.GetCachedOpenPullRequests(identity);
        var fetchedAt = new[]
        {
            repository?.FetchedAt,
            releases?.FetchedAt,
            runs?.FetchedAt,
            pulls?.FetchedAt
        }.Where(value => value.HasValue).Min();
        if (fetchedAt is null)
        {
            return;
        }

        if (repository?.Data is not null)
        {
            UpdateRepositorySummary(repository, localPath);
        }

        if (releases?.Data is { } releaseItems)
        {
            ReleasesList.ItemsSource = releaseItems
                .OrderBy(release => ReleaseChannelOrder(GetReleaseChannel(release.TagName)))
                .Select(release => new ReleaseRow(
                    release.Name ?? release.TagName,
                    GetReleaseChannel(release.TagName),
                    FormatDate(release.PublishedAt),
                    release.HtmlUrl))
                .ToArray();
            ReleasesEmpty.Visibility = releaseItems.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ReleasesList.Visibility = releaseItems.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        if (runs?.Data?.Runs is { } workflowRuns)
        {
            RunsList.ItemsSource = workflowRuns
                .Select(run => new RunRow(
                    run.Name ?? $"Workflow run #{run.RunNumber}",
                    $"{run.Status ?? "unknown"}{(string.IsNullOrWhiteSpace(run.Conclusion) ? string.Empty : $" · {run.Conclusion}")} · {run.Branch ?? "branch unavailable"}",
                    FormatDate(run.StartedAt ?? run.CreatedAt),
                    run.HtmlUrl))
                .ToArray();
            RunsEmpty.Visibility = workflowRuns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RunsList.Visibility = workflowRuns.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        if (pulls?.Data is { } pullItems)
        {
            PullRequestsList.ItemsSource = pullItems
                .Select(pull => new PullRequestRow(
                    $"#{pull.Number} · {pull.Title}",
                    $"{(pull.Draft ? "Draft" : "Open")} · checks refreshing",
                    FormatDate(pull.UpdatedAt),
                    pull.HtmlUrl))
                .ToArray();
            PullRequestsEmpty.Visibility = pullItems.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            PullRequestsList.Visibility = pullItems.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        var stale = new[] { repository?.IsStale, releases?.IsStale, runs?.IsStale, pulls?.IsStale }
            .Any(value => value == true);
        var suffix = stale || forceRefresh ? " · refreshing GitHub…" : string.Empty;
        ShowState(
            $"Showing cached GitHub data from {fetchedAt.Value.ToLocalTime():g}{suffix}",
            stale ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
    }

    private void ShowDataState(
        GitHubDataResult<GitHubRepository> repository,
        GitHubDataResult<GitHubRelease[]> releases,
        GitHubDataResult<GitHubWorkflowRuns> runs,
        GitHubDataResult<GitHubPullRequestSummary[]> pulls)
    {
        var results = new[]
        {
            (Name: "repository", ErrorKind: repository.ErrorKind, Message: repository.Message, IsStale: repository.IsStale),
            (Name: "releases", ErrorKind: releases.ErrorKind, Message: releases.Message, IsStale: releases.IsStale),
            (Name: "workflow runs", ErrorKind: runs.ErrorKind, Message: runs.Message, IsStale: runs.IsStale),
            (Name: "pull requests", ErrorKind: pulls.ErrorKind, Message: pulls.Message, IsStale: pulls.IsStale)
        };
        var error = results.FirstOrDefault(item => item.ErrorKind != GitHubErrorKind.None);
        if (error.ErrorKind != GitHubErrorKind.None)
        {
            var severity = error.ErrorKind switch
            {
                GitHubErrorKind.Authentication or GitHubErrorKind.Forbidden => InfoBarSeverity.Warning,
                GitHubErrorKind.RateLimited => InfoBarSeverity.Warning,
                GitHubErrorKind.Network or GitHubErrorKind.TimedOut or GitHubErrorKind.Server => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Error
            };
            ShowState($"{error.Name}: {error.Message ?? "GitHub data is unavailable."}", severity);
            return;
        }

        if (results.Any(item => item.IsStale))
        {
            var timestamp = new[] { repository.FetchedAt, releases.FetchedAt, runs.FetchedAt, pulls.FetchedAt }
                .Where(value => value.HasValue)
                .Min();
            ShowState(
                $"Offline — showing cached GitHub data from {timestamp?.ToLocalTime():g}.",
                InfoBarSeverity.Warning);
            return;
        }

        var empties = new[]
        {
            releases.Data is { Length: 0 } ? "no published releases" : null,
            runs.Data?.Runs is { Count: 0 } ? "no workflow runs" : null,
            pulls.Data is { Length: 0 } ? "no open pull requests" : null
        }.Where(value => value is not null);
        ShowState(empties.Any()
            ? $"Successfully checked GitHub; {string.Join(", ", empties)}."
            : "GitHub data is up to date.", InfoBarSeverity.Success);
    }

    private static string GetReleaseChannel(string tag)
    {
        if (ReleaseChannels.TryParseTag(tag, out _, out var channel))
        {
            return channel;
        }

        return "Other · unclassified";
    }

    private static int ReleaseChannelOrder(string channel) => channel switch
    {
        ReleaseChannels.Stable => 0,
        ReleaseChannels.Beta => 1,
        ReleaseChannels.Dev => 2,
        _ => 3
    };

    private static string FormatDate(DateTimeOffset? date) =>
        date?.ToLocalTime().ToString("g") ?? "Date unavailable";

    private static string FormatRateLimit(GitHubRateLimit? rate)
    {
        if (rate is null)
        {
            return "Rate-limit details unavailable.";
        }

        var parts = new System.Collections.Generic.List<string>();
        if (rate.Remaining is { } remaining)
        {
            parts.Add($"{remaining} requests remaining");
        }

        if (rate.ResetsAt is { } reset)
        {
            parts.Add($"resets {reset.ToLocalTime():t}");
        }

        if (rate.RetryAfter is { } retry)
        {
            parts.Add($"retry after {retry.TotalMinutes:0} min");
        }

        return parts.Count == 0 ? "Rate-limit details unavailable." : string.Join(" · ", parts);
    }

    private async void RunsList_ItemClick(object sender, ItemClickEventArgs e) =>
        await OpenLinkAsync((e.ClickedItem as RunRow)?.Url);

    private async void ReleasesList_ItemClick(object sender, ItemClickEventArgs e) =>
        await OpenLinkAsync((e.ClickedItem as ReleaseRow)?.Url);

    private async void PullRequestsList_ItemClick(object sender, ItemClickEventArgs e) =>
        await OpenLinkAsync((e.ClickedItem as PullRequestRow)?.Url);

    private async Task OpenLinkAsync(string? value)
    {
        if (!AppServices.GitHub.TryCreateWebUri(value, out var uri) || uri is null)
        {
            ShowState("GitHub returned a link that DevDesk will not open.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            await Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warning(ex, "Could not open a GitHub page in the browser");
            ShowState("The GitHub page could not be opened in the browser.", InfoBarSeverity.Error);
        }
    }

    private void SetLoading(bool loading)
    {
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !loading && RepositoryPicker.SelectedItem is not null;
        ReleaseButton.IsEnabled = !loading && CanExecuteRelease;
        RepositoryPicker.IsEnabled = !loading;
    }

    private void ClearRows()
    {
        RunsList.ItemsSource = null;
        ReleasesList.ItemsSource = null;
        PullRequestsList.ItemsSource = null;
        RunsList.Visibility = Visibility.Visible;
        ReleasesList.Visibility = Visibility.Visible;
        PullRequestsList.Visibility = Visibility.Visible;
        RunsEmpty.Visibility = Visibility.Collapsed;
        ReleasesEmpty.Visibility = Visibility.Collapsed;
        PullRequestsEmpty.Visibility = Visibility.Collapsed;
    }

    private void ShowState(string message, InfoBarSeverity severity)
    {
        StateInfoBar.Message = message;
        StateInfoBar.Severity = severity;
        StateInfoBar.IsOpen = true;
    }

    private sealed record ReleaseProjectFileOption(string Name, string Path);
    private sealed record ReleaseChannelOption(string Name, ReleaseChannel Value);
    private sealed record ReleaseBumpOption(string Name, ReleaseBumpKind Value);
    private sealed record RunRow(string Title, string Detail, string DateText, string Url);
    private sealed record ReleaseRow(string Title, string Channel, string DateText, string Url);
    private sealed record PullRequestRow(string Title, string Detail, string DateText, string Url);
}
