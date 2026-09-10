using DevDesk.Models;
using Xunit;

namespace DevDesk.Tests;

public class ProjectModelTests
{
    [Fact]
    public void VersionDisplay_PrefixesWithV()
    {
        Assert.Equal("v0.0.1", Project().VersionDisplay);
    }

    [Fact]
    public void WorkspaceLabel_ReflectsIsWorkspace()
    {
        Assert.Equal("Project", Project().WorkspaceLabel);
        Assert.Equal("Workspace", Project(isWorkspace: true).WorkspaceLabel);
    }

    [Theory]
    [InlineData(true, "GitHub")]
    [InlineData(false, "local only")]
    public void ListSubtitle_ReflectsRemote(bool hasRemote, string expectedPart)
    {
        var p = Project(hasRemote: hasRemote);
        Assert.Contains($"branch {p.Branch}", p.ListSubtitle);
        Assert.Contains(expectedPart, p.ListSubtitle);
        Assert.Contains(p.StatusText, p.ListSubtitle);
    }

    [Fact]
    public void NoRemote_ExposesIsNotRemoteOk()
    {
        Assert.True(Project(remoteOk: false).IsNotRemoteOk);
        Assert.False(Project().IsNotRemoteOk);
    }

    private static ProjectModel Project(bool isWorkspace = false, bool hasRemote = true,
        bool remoteOk = true, string status = "Healthy") => new()
    {
        Name = "Probe",
        Path = @"C:\Code\projects\DevEcosystem\Probe",
        Branch = "main",
        Version = "0.0.1",
        IsWorkspace = isWorkspace,
        HasRemote = hasRemote,
        RemoteOk = remoteOk,
        StatusText = status
    };
}