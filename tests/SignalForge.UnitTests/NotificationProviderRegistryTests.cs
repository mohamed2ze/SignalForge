using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Notifications;

namespace SignalForge.UnitTests;

public class NotificationProviderRegistryTests
{
    private static INotificationProvider Email() =>
        new EmailNotificationProvider(NullLogger<EmailNotificationProvider>.Instance);

    private static INotificationProvider Sms() =>
        new SmsNotificationProvider(NullLogger<SmsNotificationProvider>.Instance);

    [Fact]
    public void Resolves_providers_case_insensitively()
    {
        var registry = new NotificationProviderRegistry([Email(), Sms()]);

        Assert.NotNull(registry.Get("email"));
        Assert.NotNull(registry.Get("EMAIL"));
        Assert.NotNull(registry.Get("Sms"));
    }

    [Fact]
    public void Unknown_provider_returns_null()
    {
        var registry = new NotificationProviderRegistry([Email()]);

        Assert.Null(registry.Get("push"));
    }

    [Fact]
    public void Duplicate_provider_type_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new NotificationProviderRegistry([Email(), Email()]));
    }

    [Fact]
    public void AvailableProviders_are_sorted()
    {
        var registry = new NotificationProviderRegistry([Sms(), Email()]);

        Assert.Equal(["email", "sms"], registry.AvailableProviders);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null!)]
    public void Blank_provider_type_throws(string? providerType)
    {
        var registry = new NotificationProviderRegistry([Email()]);

        if (providerType is null)
        {
            Assert.Throws<ArgumentNullException>(() => registry.Get(providerType!));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => registry.Get(providerType));
        }
    }
}