using System.Reflection;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class GenerateSettingsTests
{
    [Fact]
    public void Validate_AllowsInfiniteSessionsFlagInNonInteractiveMode()
    {
        var settings = new GenerateSettings
        {
            NonInteractive = true,
            Newsletter = "copilot",
            Model = "gpt-5.3-codex",
            DaysBack = 7,
            InfiniteSessions = true
        };

        var result = settings.Validate();

        Assert.True(result.Successful);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateSessionConfig_MapsInfiniteSessionsEnabledValue(bool enabled)
    {
        var service = new NewsletterService(NullLogger<NewsletterService>.Instance);
        var method = typeof(NewsletterService).GetMethod(
            "CreateSessionConfig",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var config = (SessionConfig)method.Invoke(service, ["gpt-5.3-codex", "system prompt", enabled])!;

        Assert.NotNull(config.InfiniteSessions);
        Assert.Equal(enabled, config.InfiniteSessions.Enabled);
    }
}
