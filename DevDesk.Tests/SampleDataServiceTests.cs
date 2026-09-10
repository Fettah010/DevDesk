using DevDesk.Models;
using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public class SampleDataServiceTests
{
    [Fact]
    public void GetAll_OnlyIncludesProjectsInsideEcosystem()
    {
        var projects = SampleDataService.GetAll();
        Assert.NotEmpty(projects);

        foreach (var p in projects)
        {
            Assert.StartsWith(SampleDataService.ScanRoot, p.Path);
        }
    }

    [Fact]
    public void GetAll_DoesNotIncludeDevDeskItself()
    {
        Assert.DoesNotContain(SampleDataService.GetAll(), p => p.Name == "DevDesk");
    }

    [Fact]
    public void GetAll_TemplateEntryLivesInRenamedEcosystemFolder()
    {
        var template = Assert.Single(SampleDataService.GetAll());
        Assert.Equal("DevTem WinUI 3", template.Name);
        Assert.Equal(Path.Combine(SampleDataService.ScanRoot, "DevTem WinUI 3"), template.Path);
    }

    [Fact]
    public void GetSelf_IsADedicatedWorkspaceWithComponents()
    {
        var self = SampleDataService.GetSelf();
        Assert.Equal("DevDesk", self.Name);
        Assert.True(self.IsWorkspace);
        Assert.NotEmpty(self.Components);

        Assert.Contains(self.Components, c => c.Kind == "app");
        Assert.Contains(self.Components, c => c.Kind == "tests");
        Assert.Contains(self.Components, c => c.Kind == "docs");
    }

    [Fact]
    public void AllProjects_HaveRequiredFieldsAndRecentCommits()
    {
        foreach (var p in SampleDataService.GetAll().Append(SampleDataService.GetSelf()))
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.Path));
            Assert.False(string.IsNullOrWhiteSpace(p.Branch));
            Assert.False(string.IsNullOrWhiteSpace(p.Version));
            Assert.NotEmpty(p.RecentCommits);
            Assert.All(p.RecentCommits, c => Assert.False(string.IsNullOrWhiteSpace(c.Hash)));
        }
    }

    [Theory]
    [InlineData("app")]
    [InlineData("tests")]
    [InlineData("docs")]
    [InlineData("scripts")]
    [InlineData("workflow")]
    public void ComponentGlyphs_AreMappedForEveryKnownKind(string kind)
    {
        var component = new ComponentInfo { Name = "X", RelativePath = "X", Kind = kind };
        Assert.False(string.IsNullOrWhiteSpace(component.Glyph));
    }
}