using System.Security.Cryptography;
using System.Text.Json;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Notifications.Push;
using BayiOS.Modules.Notifications;
using BayiOS.Modules.Notifications.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BayiOS.IntegrationTests.Notifications;

/// <summary>
/// <c>Push:*</c> wiring (#44): off by default (no Firebase project yet), the real FCM sender only with a valid
/// service-account key, and a misconfiguration fails at startup instead of on the first assignment. The generated key
/// below is a throwaway RSA key — never a real one.
/// </summary>
public sealed class PushConfigurationTests(ApiFixture fixture)
{
    [Fact]
    public void Push_is_off_by_default()
    {
        Assert.IsType<DisabledPushSender>(fixture.Services.GetRequiredService<IPushSender>());
    }

    [Fact]
    public async Task Enabled_push_with_a_service_account_key_uses_the_fcm_sender()
    {
        using var rsa = RSA.Create(2048);
        var key = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account",
            ["project_id"] = "bayios-ci-only",
            ["private_key_id"] = "ci",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["client_email"] = "ci@bayios-ci-only.iam.gserviceaccount.com",
            ["token_uri"] = "https://oauth2.googleapis.com/token",
        });
        await using var host = fixture.Derive(builder => builder
            .UseSetting("Push:Enabled", "true")
            .UseSetting("Push:ServiceAccountJson", key));

        var sender = host.Services.GetRequiredService<IPushSender>();
        Assert.IsType<FcmPushSender>(sender);
        Assert.True(sender.IsEnabled);
    }

    [Fact]
    public void Enabled_push_without_a_key_fails_options_validation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Push:Enabled"] = "true" }).Build();
        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddNotificationsModule()
            .BuildServiceProvider();

        // ValidateOnStart runs this check when the host starts: the API refuses to start half-configured.
        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<PushOptions>>().Value);
        Assert.Contains("Push:Enabled=true needs the Firebase service-account key", error.Message, StringComparison.Ordinal);
    }
}
