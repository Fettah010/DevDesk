using DevDesk.Services;
using Xunit;

namespace DevDesk.Tests;

public sealed class ProjectOrganizationStoreTests
{
    [Fact]
    public void OrganizationMetadata_RoundTripsByCanonicalCheckoutPath()
    {
        using var sandbox = new SettingsSandbox();
        var checkout = Path.Combine(sandbox.Directory, "repo");
        var store = new ProjectOrganizationStore(sandbox.Directory);

        Assert.True(store.SetPinned(checkout, true));
        Assert.True(store.SetTags(checkout, new[] { " platform ", "infra", "PLATFORM" }));
        Assert.True(store.MarkOpened(checkout));

        var saved = new ProjectOrganizationStore(sandbox.Directory).Get(Path.Combine(checkout, "."));

        Assert.True(saved.IsPinned);
        Assert.Equal(new[] { "platform", "infra" }, saved.Tags);
        Assert.NotNull(saved.LastOpened);
    }

    [Fact]
    public void OrganizationMetadata_UnpinAndClearTagsRemovesUnusedEntry()
    {
        using var sandbox = new SettingsSandbox();
        var checkout = Path.Combine(sandbox.Directory, "repo");
        var store = new ProjectOrganizationStore(sandbox.Directory);

        Assert.True(store.SetPinned(checkout, true));
        Assert.True(store.SetPinned(checkout, false));
        Assert.True(store.SetTags(checkout, Array.Empty<string>()));

        Assert.Empty(store.Snapshot);
        Assert.False(new ProjectOrganizationStore(sandbox.Directory).Get(checkout).IsPinned);
    }

    [Fact]
    public void OrganizationMetadata_SavesViewsArchivePreferenceAndTagColour()
    {
        using var sandbox = new SettingsSandbox();
        var checkout = Path.Combine(sandbox.Directory, "repo");
        var store = new ProjectOrganizationStore(sandbox.Directory);
        var view = new SavedProjectView("Dirty work", "feature", ProjectSortOrder.Health);

        Assert.True(store.SetTags(checkout, new[] { "infra" }, "Teal"));
        Assert.True(store.SetArchived(checkout, true));
        Assert.True(store.SetHideArchived(true));
        Assert.True(store.SaveView(view));

        var reopened = new ProjectOrganizationStore(sandbox.Directory);
        var saved = reopened.Get(checkout);
        Assert.True(saved.IsArchived);
        Assert.Equal("Teal", saved.TagColor);
        Assert.True(reopened.HideArchived);
        Assert.Equal(view, Assert.Single(reopened.SavedViews));
        Assert.True(reopened.RemoveView(view.Name));
        Assert.Empty(new ProjectOrganizationStore(sandbox.Directory).SavedViews);
    }
}
