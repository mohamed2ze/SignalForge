using SignalForge.Domain.ValueObjects;

namespace SignalForge.UnitTests.Domain;

public class TenantIdTests
{
    [Fact]
    public void Wraps_supplied_guid()
    {
        var guid = Guid.NewGuid();
        var id = new TenantId(guid);

        Assert.Equal(guid, id.Value);
    }

    [Fact]
    public void New_produces_non_empty_id()
    {
        Assert.False(TenantId.New().IsEmpty());
    }

    [Fact]
    public void Empty_is_empty()
    {
        Assert.True(TenantId.Empty().IsEmpty());
    }

    [Fact]
    public void ToString_returns_guid_string()
    {
        var guid = Guid.NewGuid();
        Assert.Equal(guid.ToString(), new TenantId(guid).ToString());
    }

    [Fact]
    public void Implicitly_converts_to_guid()
    {
        var guid = Guid.NewGuid();
        Guid converted = new TenantId(guid);

        Assert.Equal(guid, converted);
    }

    [Fact]
    public void Explicitly_converts_from_guid()
    {
        var guid = Guid.NewGuid();
        var id = (TenantId)guid;

        Assert.Equal(guid, id.Value);
    }

    [Fact]
    public void Equality_is_based_on_value()
    {
        var guid = Guid.NewGuid();
        var a = new TenantId(guid);
        var b = new TenantId(guid);
        var c = TenantId.New();

        Assert.True(a == b);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a != c);
    }
}

public class ApiKeyIdTests
{
    [Fact]
    public void Create_wraps_guid()
    {
        var guid = Guid.NewGuid();
        var id = ApiKeyId.Create(guid);

        Assert.Equal(guid, id.Value);
        Assert.False(id.IsEmpty);
    }

    [Fact]
    public void Empty_is_empty()
    {
        Assert.True(ApiKeyId.Empty.IsEmpty);
        Assert.False(ApiKeyId.Empty.IsEmpty == false);
    }

    [Fact]
    public void Parse_accepts_valid_guid_string()
    {
        var guid = Guid.NewGuid();
        var id = ApiKeyId.Parse(guid.ToString());

        Assert.Equal(guid, id.Value);
    }

    [Fact]
    public void Parse_rejects_invalid_string()
    {
        Assert.Throws<FormatException>(() => ApiKeyId.Parse("not-a-guid"));
    }

    [Fact]
    public void ToString_returns_guid_string()
    {
        var guid = Guid.NewGuid();
        Assert.Equal(guid.ToString(), ApiKeyId.Create(guid).ToString());
    }

    [Fact]
    public void Equality_is_based_on_value()
    {
        var guid = Guid.NewGuid();
        var a = ApiKeyId.Create(guid);
        var b = new ApiKeyId(guid);
        var c = ApiKeyId.Empty;

        Assert.True(a == b);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a != c);
    }
}