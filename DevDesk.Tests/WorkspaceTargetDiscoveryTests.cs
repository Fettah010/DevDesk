using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public sealed class WorkspaceTargetDiscoveryTests
{
    [Fact]
    public void Discovery_FindsSolutionsAndProjectsInDeterministicOrder()
    {
        using var sandbox = new SettingsSandbox();
        var root = Path.Combine(sandbox.Directory, "workspace");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Root.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "Workspace.sln"), string.Empty);
        var app = Path.Combine(root, "src", "App");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project />");

        var targets = WorkspaceTargetDiscovery.Discover(root);

        Assert.Equal(
            new[] { "Workspace.sln", "Root.csproj", "App.csproj" },
            targets.Select(target => target.Name));
        Assert.Equal(new[] { "Solution", "Project", "Project" }, targets.Select(target => target.Kind));
    }

    [Fact]
    public void Discovery_SkipsGeneratedHiddenAndLinkedFolders()
    {
        using var sandbox = new SettingsSandbox();
        var root = Path.Combine(sandbox.Directory, "workspace");
        var generated = Path.Combine(root, "obj");
        var hidden = Path.Combine(root, ".hidden");
        Directory.CreateDirectory(generated);
        Directory.CreateDirectory(hidden);
        File.WriteAllText(Path.Combine(generated, "Generated.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(hidden, "Hidden.sln"), string.Empty);

        var targets = WorkspaceTargetDiscovery.Discover(root);

        Assert.Empty(targets);
    }
}
