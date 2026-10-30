using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DevDesk.Models;
using DevDesk.Services;

namespace DevDesk.Pages;

public sealed partial class ProjectsPage : Page
{
    private readonly GitService _git = new();
    private CancellationTokenSource? _detailsCancellation;
    private CancellationTokenSource? _operationCancellation;
    private bool _operationRunning;
    private string? _conflictOperation;
    private bool _conflictsResolved;
    private int _historyPage;
    private ObservableCollection<CommitInfo> _commits = new();
    private GitRepositoryStatus? _latestStatus;
    private IReadOnlyList<CommitInfo>? _latestHistory;
    private IReadOnlyList<GitRemoteInfo>? _latestRemotes;

    public ProjectsPage()
    {
        this.InitializeComponent();
        _ = LoadProjectsAsync();
    }

    /// <summary>The project the detail pane is showing, for the open actions.</summary>
    private ProjectModel? Selected => ProjectsList.SelectedItem as ProjectModel;

    private async Task LoadProjectsAsync()
    {
        try
        {
            var snapshot = await AppServices.Catalog.GetOrRefreshAsync();
            ProjectsList.ItemsSource = snapshot.Projects;
            if (ProjectsList.Items.Count > 0)
                ProjectsList.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ShowError("Could not read the project list", ex.Message);
        }
    }

    private void ProjectsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectsList.SelectedItem is not ProjectModel p)
            return;

        DetailIcon.Glyph = p.IconGlyph;
        DetailTitle.Text = p.Name;
        DetailPath.Text = p.Path;
        DetailWorkspaceBadge.Visibility = p.IsWorkspace ? Visibility.Visible : Visibility.Collapsed;

        OverviewBranch.Text = p.Branch;
        OverviewVersion.Text = p.VersionDisplay;

        // "No remote" is a fact about this folder, not a promise about a future
        // version: a workspace like this one has no remote and works fine.
        OverviewRepo.Text = p.HasRemote
            ? $"{p.RemoteHost} — remote configured"
            : "No remote — everything here is local";

        OverviewTree.Text = p.Dirty ? "dirty — uncommitted changes present" : "clean";
        OverviewCommit.Text = p.RecentCommits.FirstOrDefault()?.Subject ?? "—";
        _ = LoadGitDetailsAsync(p);
    }

    private async Task LoadGitDetailsAsync(ProjectModel project)
    {
        _detailsCancellation?.Cancel();
        _detailsCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _detailsCancellation = cancellation;
        _latestStatus = null;
        _latestHistory = null;
        _latestRemotes = null;

        ChangesList.ItemsSource = null;
        CommitsList.ItemsSource = null;
        BranchesList.ItemsSource = null;
        TagsList.ItemsSource = null;
        ChangesEmpty.Visibility = Visibility.Collapsed;
        CommitsEmpty.Visibility = Visibility.Collapsed;
        BranchesEmpty.Visibility = Visibility.Collapsed;
        _historyPage = 0;
        LoadOlderCommitsButton.Visibility = Visibility.Collapsed;
        CommitDiffstat.Text = string.Empty;
        FileDiffPreview.Text = string.Empty;
        PageInfoBar.IsOpen = false;
        ConflictPanel.Visibility = Visibility.Collapsed;
        OperationStatus.Visibility = Visibility.Visible;
        OperationMessage.Text = "Reading Git details…";
        SetOperationButtonsEnabled(false);

        try
        {
            var statusTask = _git.GetStatusAsync(project.Path, cancellation.Token);
            var historyTask = _git.GetHistoryAsync(project.Path, cancellationToken: cancellation.Token);
            var branchesTask = _git.GetBranchesAsync(project.Path, cancellation.Token);
            var conflictTask = _git.GetConflictStateAsync(project.Path, cancellation.Token);
            var remotesTask = _git.GetRemotesAsync(project.Path, cancellation.Token);
            var tagsTask = _git.GetTagsAsync(project.Path, cancellation.Token);
            await Task.WhenAll(statusTask, historyTask, branchesTask, conflictTask, remotesTask, tagsTask);

            if (cancellation.IsCancellationRequested || !ReferenceEquals(_detailsCancellation, cancellation))
                return;

            var status = await statusTask;
            var history = await historyTask;
            var branches = await branchesTask;
            var conflict = await conflictTask;
            var remotes = await remotesTask;
            var tags = await tagsTask;
            if (!status.Operation.Succeeded)
            {
                LoggingService.Log.Warning("Projects Git status for {Project} failed: {Error}", project.Name, status.Operation.Error);
                ShowError("Git details unavailable", status.Operation.Error);
                return;
            }

            var repositoryStatus = status.Value!;
            _latestStatus = repositoryStatus;
            _latestHistory = history.Value!;
            _latestRemotes = remotes.Value!;
            OverviewBranch.Text = repositoryStatus.Branch ?? "detached";
            OverviewTree.Text = repositoryStatus.IsBareRepository
                ? "bare repository — no working tree"
                : repositoryStatus.Files.Count > 0 ? "dirty — uncommitted changes present" : "clean";
            ChangesEmpty.Text = repositoryStatus.IsBareRepository ? "This bare repository has no working tree." : "Working tree is clean.";
            SetOperationButtonsEnabled(!_operationRunning);

            var files = repositoryStatus.Files;
            ChangesList.ItemsSource = files;
            ChangesEmpty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _commits = new ObservableCollection<CommitInfo>(history.Value!);
            CommitsList.ItemsSource = _commits;
            CommitsEmpty.Visibility = history.Value!.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadOlderCommitsButton.Visibility = history.Value.Count == 50 ? Visibility.Visible : Visibility.Collapsed;
            BranchesList.ItemsSource = branches.Value;
            BranchesEmpty.Visibility = branches.Value!.All(branch => branch.IsRemote) ? Visibility.Visible : Visibility.Collapsed;
            TagsList.ItemsSource = tags.Value;
            TagsEmpty.Visibility = tags.Value!.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RemoteSummary.Text = remotes.Value!.Count == 0
                ? "No remotes configured."
                : "Remotes: " + string.Join(" · ", remotes.Value.Select(remote => $"{remote.Name} ({remote.Direction}) {remote.Url}"));
            TrackingSummary.Text = repositoryStatus.Upstream is null
                ? $"{repositoryStatus.Branch ?? "Detached HEAD"} · no upstream · local branch"
                : $"{repositoryStatus.Branch} → {repositoryStatus.Upstream} · ahead {repositoryStatus.Ahead}, behind {repositoryStatus.Behind}";

            _conflictOperation = conflict.Value?.Operation;
            var conflictedFiles = files.Where(file => file.Kind == "conflict").ToArray();
            _conflictsResolved = conflictedFiles.Length == 0;
            ConflictFiles.ItemsSource = conflictedFiles;
            ConflictPanel.Visibility = conflict.Value?.HasConflict == true ? Visibility.Visible : Visibility.Collapsed;
            ConflictSummary.Text = $"{_conflictOperation ?? "Git"} operation needs attention · {conflictedFiles.Length} conflicted file(s)";
            ContinueConflictButton.IsEnabled = _conflictOperation is not null && _conflictsResolved;

            var secondaryFailure = !history.Operation.Succeeded ? history.Operation
                : !branches.Operation.Succeeded ? branches.Operation
                : !remotes.Operation.Succeeded ? remotes.Operation
                : !tags.Operation.Succeeded ? tags.Operation
                : null;
            if (secondaryFailure is not null)
                ShowError("Some Git details could not be loaded", secondaryFailure.Error);
        }
        catch (OperationCanceledException)
        {
            // A project selection changed while the previous read was in flight.
        }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
                ShowError("Git details could not be loaded", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_detailsCancellation, cancellation) && !_operationRunning)
                OperationStatus.Visibility = Visibility.Collapsed;
        }
    }

    private void DetailTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OverviewPanel.Visibility = DetailTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangesPanel.Visibility = DetailTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        CommitsPanel.Visibility = DetailTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        BranchesPanel.Visibility = DetailTabs.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BranchesList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SetOperationButtonsEnabled(!_operationRunning && _latestStatus is not null);

    private void TagsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SetOperationButtonsEnabled(!_operationRunning && _latestStatus is not null);

    private async void ChangesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SetOperationButtonsEnabled(!_operationRunning && _latestStatus is not null);
        var project = Selected;
        var file = SelectedChanges().FirstOrDefault();
        if (project is null || file is null)
        {
            FileDiffPreview.Text = string.Empty;
            return;
        }
        if (file.Kind == "untracked")
        {
            FileDiffPreview.Text = "Untracked files do not have a Git diff until staged.";
            return;
        }
        var request = _detailsCancellation;
        var result = await _git.GetFileDiffAsync(project.Path, file.Path, file.IndexStatus != ".",
            request?.Token ?? CancellationToken.None);
        if (ReferenceEquals(request, _detailsCancellation) && SelectedChanges().Any(item => item.Path == file.Path))
            FileDiffPreview.Text = result.Succeeded ? result.Output : result.Error;
    }

    private async void CommitsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } project || CommitsList.SelectedItem is not CommitInfo commit) return;
        var request = _detailsCancellation;
        var result = await _git.GetCommitDiffstatAsync(project.Path, commit.Hash, request?.Token ?? CancellationToken.None);
        if (ReferenceEquals(request, _detailsCancellation) && CommitsList.SelectedItem is CommitInfo selected && selected.Hash == commit.Hash)
            CommitDiffstat.Text = result.Succeeded ? result.Output : result.Error;
    }

    private async void LoadOlderCommitsButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null || _operationRunning) return;
        var nextPage = _historyPage + 1;
        OperationStatus.Visibility = Visibility.Visible;
        OperationMessage.Text = "Loading commit history…";
        try
        {
            var result = await _git.GetHistoryAsync(project.Path, nextPage, cancellationToken: _detailsCancellation?.Token ?? CancellationToken.None);
            if (!result.Operation.Succeeded) { ShowError("Could not load more history", result.Operation.Error); return; }
            foreach (var commit in result.Value!) _commits.Add(commit);
            _historyPage = nextPage;
            LoadOlderCommitsButton.Visibility = result.Value.Count == 50 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            OperationStatus.Visibility = Visibility.Collapsed;
        }
    }

    private async void StageSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedPaths();
        if (paths.Length == 0) { ShowError("Select changes first", "Choose one or more files in the list."); return; }
        var project = Selected;
        if (project is null) return;
        await RunGitOperationAsync(project, "Stage selected files", "Add these paths to the Git index.",
            FormatCommand(new[] { "git", "add", "--" }.Concat(paths)),
            token => _git.StageAsync(project.Path, paths, token));
    }

    private async void StageAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } project) return;
        await RunGitOperationAsync(project, "Stage all changes", "Add all tracked and untracked changes to the Git index.",
            "git add --all", token => _git.StageAllAsync(project.Path, token));
    }

    private async void RefreshGitButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } project || _operationRunning) return;
        await LoadGitDetailsAsync(project);
        PublishProjectState(project);
    }

    private async void UnstageSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var files = SelectedChanges().Where(file => file.IndexStatus != "." && file.IndexStatus != "?").ToArray();
        if (files.Length == 0) { ShowError("No staged files selected", "Select at least one staged path."); return; }
        var project = Selected;
        if (project is null) return;
        var paths = files.Select(file => file.Path).ToArray();
        await RunGitOperationAsync(project, "Unstage selected files", "Remove these paths from the index while keeping their working tree content.",
            FormatCommand(new[] { "git", "restore", "--staged", "--" }.Concat(paths)),
            token => _git.UnstageAsync(project.Path, paths, token));
    }

    private async void DiscardSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var files = SelectedChanges().Where(file => file.Kind != "untracked").ToArray();
        if (files.Length == 0) { ShowError("No tracked changes selected", "Select tracked files to restore from HEAD."); return; }
        var project = Selected;
        if (project is null) return;
        var paths = files.Select(file => file.Path).ToArray();
        await RunGitOperationAsync(project, "Discard tracked changes", "Restore selected tracked files from HEAD. This cannot recover their current edits.",
            FormatCommand(new[] { "git", "restore", "--worktree", "--" }.Concat(paths)),
            token => _git.DiscardTrackedChangesAsync(project.Path, paths, token), project.Name);
    }

    private async void CommitButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null) return;

        var message = new TextBox { PlaceholderText = "Describe the change", AcceptsReturn = true, MinHeight = 72, TextWrapping = TextWrapping.Wrap };
        var amend = new CheckBox { Content = "Amend previous commit" };
        var signOff = new CheckBox { Content = "Add sign-off" };
        var pushAfter = new CheckBox { Content = "Push after commit" };
        var guidance = new TextBlock { Text = "Keep the first line concise. Longer messages remain valid.", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        message.TextChanged += (_, _) => guidance.Text = message.Text.Length > 72
            ? $"First line is {message.Text.Split('\n')[0].Length} characters. Longer messages remain valid."
            : "Keep the first line concise. Longer messages remain valid.";
        amend.Checked += (_, _) => message.Text = project.RecentCommits.FirstOrDefault()?.Subject ?? string.Empty;
        amend.Unchecked += (_, _) => message.Text = string.Empty;
        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(new TextBlock { Text = "Commit message", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        form.Children.Add(message);
        form.Children.Add(guidance);
        form.Children.Add(amend);
        form.Children.Add(signOff);
        form.Children.Add(pushAfter);

        var dialog = new ContentDialog
        {
            Title = $"Commit in {project.Name}",
            Content = form,
            PrimaryButtonText = "Review command",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                guidance.Text = "Enter a commit message to continue.";
                args.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var args = new List<string> { "git", "commit" };
        if (amend.IsChecked == true) args.Add("--amend");
        if (signOff.IsChecked == true) args.Add("--signoff");
        args.Add("-m");
        args.Add(message.Text);
        var preview = FormatCommand(args);
        var activeBranch = BranchesList.Items.OfType<GitBranchInfo>().FirstOrDefault(item => item.IsCurrent);
        if (pushAfter.IsChecked == true)
        {
            if (_latestRemotes?.Count is not > 0)
            {
                ShowError("No remote configured", "Add an origin remote before enabling push after commit.");
                return;
            }
            var branchName = activeBranch?.Name ?? _latestStatus?.Branch ?? "<current-branch>";
            var pushArgs = activeBranch?.Upstream is null
                ? new[] { "git", "push", "--set-upstream", "origin", branchName }
                : new[] { "git", "push", "origin", branchName };
            preview += Environment.NewLine + FormatCommand(pushArgs);
        }
        await RunGitOperationAsync(project, "Create commit", "Commit the staged changes shown in the Changes list.", preview,
            async token =>
            {
                var commit = await _git.CommitAsync(project.Path, message.Text, amend.IsChecked == true, signOff.IsChecked == true, token);
                if (!commit.Succeeded || pushAfter.IsChecked != true) return commit;
                var push = await _git.PushAsync(project.Path, token);
                return push with
                {
                    Output = string.Join(Environment.NewLine, new[] { commit.Output, push.Output }.Where(text => !string.IsNullOrWhiteSpace(text))),
                    Error = push.Succeeded ? push.Error : "Commit was created, but push failed. " + push.Error,
                };
            });
    }

    private async void FetchButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null) return;
        await RunGitOperationAsync(project, "Fetch from origin", "Download remote refs. This does not change files or merge commits.",
            "git fetch origin", token => _git.FetchAsync(project.Path, token));
    }

    private async void PullButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null) return;
        await RunGitOperationAsync(project, "Fast-forward pull", "Update the current branch only when it can fast-forward cleanly.",
            "git pull --ff-only", token => _git.PullFastForwardOnlyAsync(project.Path, token));
    }

    private async void PushButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null) return;
        var branch = _latestStatus?.Branch;
        if (string.IsNullOrWhiteSpace(branch)) { ShowError("Cannot push while detached", "Switch to a named local branch before pushing."); return; }
        var hasUpstream = BranchesList.Items.OfType<GitBranchInfo>().FirstOrDefault(item => item.IsCurrent)?.Upstream is not null;
        var preview = hasUpstream ? FormatCommand(new[] { "git", "push", "origin", branch })
            : FormatCommand(new[] { "git", "push", "--set-upstream", "origin", branch });
        await RunGitOperationAsync(project, "Push current branch", "Upload commits to the configured origin.", preview,
            token => _git.PushAsync(project.Path, token));
    }

    private async void CreateBranchButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null) return;
        var name = await AskForTextAsync($"New branch in {project.Name}", "Branch name", "Create branch");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunGitOperationAsync(project, "Create and switch branch", "Create a new local branch from the current commit and switch to it.",
            FormatCommand(new[] { "git", "switch", "-c", name }), token => _git.CreateBranchAsync(project.Path, name, token));
    }

    private async void SwitchBranchButton_Click(object sender, RoutedEventArgs e)
    {
        if (BranchesList.SelectedItem is not GitBranchInfo branch || Selected is not { } project) return;
        if (branch.IsRemote) { ShowError("Remote branches are read only", "Create a local branch from the remote branch before switching."); return; }
        await RunGitOperationAsync(project, "Switch branch", "Change the checked out branch. Git will refuse if local changes would be overwritten.",
            FormatCommand(new[] { "git", "switch", branch.Name }), token => _git.SwitchBranchAsync(project.Path, branch.Name, token));
    }

    private async void DeleteBranchButton_Click(object sender, RoutedEventArgs e)
    {
        if (BranchesList.SelectedItem is not GitBranchInfo branch || Selected is not { } project) return;
        if (branch.IsRemote) { ShowError("Remote branches are read only", "Delete a local tracking branch instead."); return; }
        if (branch.IsCurrent) { ShowError("Cannot delete the current branch", "Switch to another branch first."); return; }
        await RunGitOperationAsync(project, "Delete merged branch", "Git deletes only branches already merged into the current branch.",
            FormatCommand(new[] { "git", "branch", "-d", branch.Name }), token => _git.DeleteMergedBranchAsync(project.Path, branch.Name, token), branch.Name);
    }

    private async void CreateTagButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null) return;
        var name = await AskForTextAsync($"Create local tag in {project.Name}", "Tag name", "Continue");
        if (string.IsNullOrWhiteSpace(name)) return;
        var message = await AskForTextAsync("Annotated tag message", "Tag message", "Review tag");
        if (string.IsNullOrWhiteSpace(message)) return;
        await RunGitOperationAsync(project, "Create local tag", "Create an annotated local tag. This does not upload the tag.",
            FormatCommand(new[] { "git", "tag", "-a", name, "-m", message }),
            token => _git.CreateTagAsync(project.Path, name, message, token));
    }

    private async void DeleteTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (TagsList.SelectedItem is not GitTagInfo tag || Selected is not { } project) return;
        await RunGitOperationAsync(project, "Delete local tag", "Remove this tag from the local repository. No remote tag is changed.",
            FormatCommand(new[] { "git", "tag", "-d", tag.Name }), token => _git.DeleteLocalTagAsync(project.Path, tag.Name, token), tag.Name);
    }

    private async void AbortConflictButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null || _conflictOperation is null || !_conflictsResolved) return;
        var merge = _conflictOperation == "merge";
        var args = merge ? new[] { "git", "merge", "--abort" } : new[] { "git", "rebase", "--abort" };
        await RunGitOperationAsync(project, $"Abort {_conflictOperation}", "Discard the in-progress merge or rebase and return to its pre-operation state.",
            FormatCommand(args), token => merge ? _git.AbortMergeAsync(project.Path, token) : _git.AbortRebaseAsync(project.Path, token), project.Name);
    }

    private async void ContinueConflictButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null || _conflictOperation is null) return;
        var merge = _conflictOperation == "merge";
        var args = merge ? new[] { "git", "-c", "core.editor=true", "merge", "--continue" }
            : new[] { "git", "-c", "core.editor=true", "rebase", "--continue" };
        await RunGitOperationAsync(project, $"Continue {_conflictOperation}", "Continue using the resolved files. Git keeps its prepared operation message.",
            FormatCommand(args), token => merge ? _git.ContinueMergeAsync(project.Path, token) : _git.ContinueRebaseAsync(project.Path, token));
    }

    private async Task RunGitOperationAsync(ProjectModel project, string title, string consequence, string command,
        Func<CancellationToken, Task<GitOperationResult>> operation, string? typedConfirmation = null)
    {
        if (_operationRunning) return;
        if (!await ConfirmOperationAsync(title, consequence, command, typedConfirmation)) return;

        _operationRunning = true;
        _operationCancellation = new CancellationTokenSource();
        ProjectsList.IsEnabled = false;
        LoggingService.Log.Information("Git operation {Operation} started for {Project}", title, project.Name);
        OperationStatus.Visibility = Visibility.Visible;
        OperationMessage.Text = title + "…";
        CancelOperationButton.IsEnabled = true;
        SetOperationButtonsEnabled(false);
        try
        {
            var result = await operation(_operationCancellation.Token);
            if (!result.Succeeded)
            {
                LoggingService.Log.Warning("Git operation {Operation} failed for {Project}: {Error}", title, project.Name, result.Error);
                await LoadGitDetailsAsync(project);
                PublishProjectState(project);
                if (result.Failure == GitFailureKind.Cancelled)
                {
                    PageInfoBar.Severity = InfoBarSeverity.Warning;
                    PageInfoBar.Title = title + " cancelled";
                    PageInfoBar.Message = "Git state was refreshed. A cancelled network transfer may have completed partially.";
                    PageInfoBar.IsOpen = true;
                    return;
                }
                ShowError(title + " failed", result.Error);
                return;
            }

            await LoadGitDetailsAsync(project);
            PublishProjectState(project);
            LoggingService.Log.Information("Git operation {Operation} completed for {Project}", title, project.Name);
            PageInfoBar.Severity = InfoBarSeverity.Success;
            PageInfoBar.Title = title;
            var output = result.Output.Trim();
            PageInfoBar.Message = output.Length == 0 ? "Completed successfully." : output.Length > 1000 ? output[..1000] + "…" : output;
            PageInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error(ex, "Git operation {Operation} failed for {Project}", title, project.Name);
            ShowError(title + " failed", GitService.Redact(ex.Message));
        }
        finally
        {
            _operationRunning = false;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            ProjectsList.IsEnabled = true;
            OperationStatus.Visibility = Visibility.Collapsed;
            CancelOperationButton.IsEnabled = false;
            SetOperationButtonsEnabled(true);
        }
    }

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

    private async Task<bool> ConfirmOperationAsync(string title, string consequence, string command, string? typedConfirmation)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = consequence, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Command preview", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = command, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap });
        TextBox? confirmation = null;
        if (typedConfirmation is not null)
        {
            content.Children.Add(new TextBlock { Text = $"Type {typedConfirmation} to confirm.", TextWrapping = TextWrapping.Wrap });
            confirmation = new TextBox { PlaceholderText = typedConfirmation };
            content.Children.Add(confirmation);
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "Run",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (confirmation is not null)
        {
            var typed = confirmation;
            dialog.PrimaryButtonClick += (_, args) => args.Cancel = !string.Equals(typed.Text, typedConfirmation, StringComparison.Ordinal);
        }
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<string?> AskForTextAsync(string title, string placeholder, string accept)
    {
        var input = new TextBox { PlaceholderText = placeholder };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = accept,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    private GitFileStatus[] SelectedChanges() => ChangesList.SelectedItems.OfType<GitFileStatus>().ToArray();
    private string[] SelectedPaths() => SelectedChanges().Select(file => file.Path).ToArray();

    private void SetOperationButtonsEnabled(bool enabled)
    {
        var canWrite = enabled && _latestStatus?.IsBareRepository != true;
        foreach (var button in new[] { StageAllButton, FetchButton, PullButton, PushButton, CreateBranchButton, CreateTagButton })
            button.IsEnabled = canWrite;
        var selectedFiles = SelectedChanges();
        StageSelectedButton.IsEnabled = canWrite && selectedFiles.Length > 0;
        UnstageSelectedButton.IsEnabled = canWrite && selectedFiles.Any(file => file.IndexStatus != "." && file.IndexStatus != "?");
        DiscardSelectedButton.IsEnabled = canWrite && selectedFiles.Any(file => file.Kind != "untracked");
        CommitButton.IsEnabled = canWrite && _latestStatus?.Files.Any(file => file.IndexStatus != "." && file.IndexStatus != "?") == true;
        RefreshGitButton.IsEnabled = enabled;
        var selectedBranch = BranchesList.SelectedItem as GitBranchInfo;
        SwitchBranchButton.IsEnabled = canWrite && selectedBranch is { IsRemote: false, IsCurrent: false };
        DeleteBranchButton.IsEnabled = canWrite && selectedBranch is { IsRemote: false, IsCurrent: false };
        DeleteTagButton.IsEnabled = canWrite && TagsList.SelectedItem is GitTagInfo;
        AbortConflictButton.IsEnabled = canWrite;
        ContinueConflictButton.IsEnabled = canWrite && _conflictOperation is not null && _conflictsResolved;
    }

    private void PublishProjectState(ProjectModel project)
    {
        if (_latestStatus is null || _latestHistory is null || _latestRemotes is null) return;
        var dirty = _latestStatus.Files.Count > 0;
        var behind = _latestStatus.Behind > 0;
        var updated = new ProjectModel
        {
            Name = project.Name,
            Path = project.Path,
            IconGlyph = project.IconGlyph,
            IsWorkspace = project.IsWorkspace,
            Branch = _latestStatus.Branch ?? "detached",
            Version = project.Version,
            VersionMismatch = project.VersionMismatch,
            HasRemote = _latestRemotes.Count > 0,
            RemoteOk = project.RemoteOk,
            RemoteHost = project.RemoteHost,
            Dirty = dirty,
            BehindUpstream = behind,
            StatusText = !project.RemoteOk ? "Remote unavailable" : dirty ? "Changes" : behind ? "Behind upstream" : "Healthy",
            RecentCommits = _latestHistory.Take(3).ToArray(),
            Components = project.Components,
        };
        if (!AppServices.Catalog.UpdateProject(updated)) return;
        var index = ProjectsList.SelectedIndex;
        ProjectsList.ItemsSource = AppServices.Catalog.Snapshot.Projects;
        if (index >= 0 && index < ProjectsList.Items.Count) ProjectsList.SelectedIndex = index;
    }

    private static string FormatCommand(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(QuoteArgument));

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;
        var result = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1);
            else result.Append('\\', slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    // The two real actions on this page. They open what already exists on disk
    // through ProjectOpenActions, exactly like the Dashboard card does; nothing
    // here writes to the repository.
    private void OpenSolutionButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null)
            return;

        var target = ProjectOpenActions.FindSolutionOrProject(project.Path);
        if (target is null)
        {
            ShowError(
                "Nothing to open",
                $"{project.Name} has no solution or project file at its root.");
            return;
        }

        if (!ProjectOpenActions.OpenWithDefaultApp(target))
        {
            ShowError("Could not open the project", target);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var project = Selected;
        if (project is null)
            return;

        if (!ProjectOpenActions.OpenInExplorer(project.Path))
        {
            ShowError("Could not open the folder", project.Path);
        }
    }

    private void ShowError(string title, string message)
    {
        PageInfoBar.Severity = InfoBarSeverity.Error;
        PageInfoBar.Title = title;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }
}
