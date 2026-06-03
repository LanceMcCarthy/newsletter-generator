using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class NewsletterServiceSessionTests
{
    [Fact]
    public void BuildSessionId_IsDeterministic_ForSameInputs()
    {
        var sessionId1 = NewsletterService.BuildSessionId("CopilotCliSdk:2026-06-01:2026-06-07", "welcome-summary", "claude-opus-4.6");
        var sessionId2 = NewsletterService.BuildSessionId("CopilotCliSdk:2026-06-01:2026-06-07", "welcome-summary", "claude-opus-4.6");

        Assert.Equal(sessionId1, sessionId2);
        Assert.StartsWith("newsletter-generator-", sessionId1);
    }

    [Fact]
    public void BuildSessionId_Changes_WhenModelChanges()
    {
        var sessionId1 = NewsletterService.BuildSessionId("CopilotCliSdk:2026-06-01:2026-06-07", "welcome-summary", "claude-opus-4.6");
        var sessionId2 = NewsletterService.BuildSessionId("CopilotCliSdk:2026-06-01:2026-06-07", "welcome-summary", "gpt-5.4");

        Assert.NotEqual(sessionId1, sessionId2);
    }

    [Fact]
    public void BuildSessionId_Changes_WhenWorkflowOrPeriodChanges()
    {
        var baseline = NewsletterService.BuildSessionId("VSCodeInsiders:2026-06-01:2026-06-07", "vscode-newsletter", "claude-opus-4.6");
        var differentWorkflow = NewsletterService.BuildSessionId("VSCodeInsiders:2026-06-01:2026-06-07", "newsletter-title", "claude-opus-4.6");
        var differentPeriod = NewsletterService.BuildSessionId("VSCodeInsiders:2026-06-08:2026-06-14", "vscode-newsletter", "claude-opus-4.6");

        Assert.NotEqual(baseline, differentWorkflow);
        Assert.NotEqual(baseline, differentPeriod);
    }
}
