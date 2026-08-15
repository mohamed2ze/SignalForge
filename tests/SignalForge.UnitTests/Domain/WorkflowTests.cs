using SignalForge.Domain.Models;

namespace SignalForge.UnitTests.Domain;

public class WorkflowTests
{
    [Fact]
    public void Create_sets_defaults()
    {
        var workflow = Workflow.Create(Guid.NewGuid(), "onboarding");

        Assert.NotEqual(Guid.Empty, workflow.Id);
        Assert.Equal("onboarding", workflow.Name);
        Assert.False(workflow.IsEnabled);
        Assert.Null(workflow.DeletedAt);
        Assert.Null(workflow.UpdatedAt);
        Assert.Empty(workflow.Versions);
    }

    [Fact]
    public void Create_trims_name()
    {
        var workflow = Workflow.Create(Guid.NewGuid(), "  onboarding  ");

        Assert.Equal("onboarding", workflow.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_name(string? name)
    {
        Assert.Throws<ArgumentException>(() => Workflow.Create(Guid.NewGuid(), name!));
    }

    [Fact]
    public void UpdateInfo_trims_and_touches_UpdatedAt()
    {
        var workflow = Workflow.Create(Guid.NewGuid(), "onboarding");

        workflow.UpdateInfo("  renewals  ", "  desc  ");

        Assert.Equal("renewals", workflow.Name);
        Assert.Equal("desc", workflow.Description);
        Assert.NotNull(workflow.UpdatedAt);
    }

    [Fact]
    public void UpdateInfo_rejects_blank_name()
    {
        var workflow = Workflow.Create(Guid.NewGuid(), "onboarding");

        Assert.Throws<ArgumentException>(() => workflow.UpdateInfo("  "));
    }

    [Fact]
    public void Enable_Disable_toggle_state()
    {
        var workflow = Workflow.Create(Guid.NewGuid(), "onboarding");

        workflow.Enable();
        Assert.True(workflow.IsEnabled);

        workflow.Disable();
        Assert.False(workflow.IsEnabled);
    }

    [Fact]
    public void Delete_soft_deletes_and_disables()
    {
        var workflow = Workflow.Create(Guid.NewGuid(), "onboarding");
        workflow.Enable();

        workflow.Delete();

        Assert.True(workflow.IsEnabled == false);
        Assert.NotNull(workflow.DeletedAt);
    }

    [Fact]
    public void CreateDraftVersion_attaches_version()
    {
        var workflow = Workflow.Create(Guid.NewGuid(), "onboarding");

        var version = workflow.CreateDraftVersion(1);

        Assert.Single(workflow.Versions);
        Assert.Same(version, workflow.Versions.First());
        Assert.Equal(1, version.VersionNumber);
    }

    [Fact]
    public void GetLatestPublishedVersion_returns_null_when_none_published()
    {
        var workflow = Workflow.Create(Guid.NewGuid(), "onboarding");
        workflow.CreateDraftVersion(1);

        Assert.Null(workflow.GetLatestPublishedVersion());
    }

    [Fact]
    public void GetLatestPublishedVersion_returns_highest_published()
    {
        var workflow = Workflow.Create(Guid.NewGuid(), "onboarding");
        workflow.CreateDraftVersion(1);
        var published = workflow.CreateDraftVersion(2);
        workflow.CreateDraftVersion(3);
        published.AddStep(1, "Delay", "{}");
        published.Publish();

        Assert.Same(published, workflow.GetLatestPublishedVersion());
    }

    [Fact]
    public void GetLatestVersion_returns_highest_version()
    {
        var workflow = Workflow.Create(Guid.NewGuid(), "onboarding");
        workflow.CreateDraftVersion(1);
        var latest = workflow.CreateDraftVersion(2);

        Assert.Same(latest, workflow.GetLatestVersion());
    }
}