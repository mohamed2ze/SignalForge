using SignalForge.Domain.Models;

namespace SignalForge.UnitTests.Domain;

public class TenantTests
{
    [Fact]
    public void Create_sets_defaults()
    {
        var tenant = Tenant.Create("Acme");

        Assert.NotEqual(Guid.Empty, tenant.Id);
        Assert.Equal("Acme", tenant.Name);
        Assert.True(tenant.IsActive);
        Assert.Null(tenant.DeletedAt);
        Assert.Null(tenant.Description);
    }

    [Fact]
    public void Create_trims_name_and_description()
    {
        var tenant = Tenant.Create("  Acme  ", "  Description  ");

        Assert.Equal("Acme", tenant.Name);
        Assert.Equal("Description", tenant.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_name(string? name)
    {
        Assert.Throws<ArgumentException>(() => Tenant.Create(name!));
    }

    [Fact]
    public void CreateWithId_uses_supplied_id()
    {
        var id = Guid.NewGuid();
        var tenant = Tenant.CreateWithId(id, "Acme");

        Assert.Equal(id, tenant.Id);
    }

    [Fact]
    public void CreateWithId_rejects_empty_id()
    {
        Assert.Throws<ArgumentException>(() => Tenant.CreateWithId(Guid.Empty, "Acme"));
    }

    [Fact]
    public void UpdateInfo_trims_and_touches_UpdatedAt()
    {
        var tenant = Tenant.Create("Acme");
        var before = DateTime.UtcNow;
        System.Threading.Thread.Sleep(5);

        tenant.UpdateInfo("  Globe  ", "  New  ");

        Assert.Equal("Globe", tenant.Name);
        Assert.Equal("New", tenant.Description);
        Assert.True(tenant.UpdatedAt >= before);
    }

    [Fact]
    public void UpdateInfo_rejects_blank_name()
    {
        var tenant = Tenant.Create("Acme");

        Assert.Throws<ArgumentException>(() => tenant.UpdateInfo("  "));
    }

    [Fact]
    public void Deactivate_soft_deletes()
    {
        var tenant = Tenant.Create("Acme");

        tenant.Deactivate();

        Assert.False(tenant.IsActive);
        Assert.NotNull(tenant.DeletedAt);
    }

    [Fact]
    public void Reactivate_restores_tenant()
    {
        var tenant = Tenant.Create("Acme");
        tenant.Deactivate();

        tenant.Reactivate();

        Assert.True(tenant.IsActive);
        Assert.Null(tenant.DeletedAt);
    }
}