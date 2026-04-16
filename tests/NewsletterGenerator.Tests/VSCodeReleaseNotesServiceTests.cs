using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class VSCodeReleaseNotesServiceTests
{
    // ── ValidateFrontMatter ───────────────────────────────────────────────────

    [Theory]
    [InlineData("---\nProductEdition: Insiders\n---\n# Content", true)]
    [InlineData("---\nProductEdition: insiders\n---\n# Content", true)]
    [InlineData("---\nProductEdition: Stable\n---\n# Content", false)]
    [InlineData("# No front matter here", false)]
    [InlineData("---\nTitle: Some Title\n---\n# Content", false)]
    public void ValidateFrontMatter_ClassifiesCorrectly(string markdown, bool expected)
    {
        Assert.Equal(expected, VSCodeReleaseNotesService.ValidateFrontMatter(markdown));
    }

    // ── TruncateTitle ─────────────────────────────────────────────────────────

    [Fact]
    public void TruncateTitle_ShortTitle_ReturnsUnchanged()
    {
        var title = "Fix terminal rendering";
        Assert.Equal(title, VSCodeReleaseNotesService.TruncateTitle(title));
    }

    [Fact]
    public void TruncateTitle_TruncatesAtFirstPeriod()
    {
        var title = "Fix terminal rendering. This also improves performance for long-running tasks.";
        Assert.Equal("Fix terminal rendering", VSCodeReleaseNotesService.TruncateTitle(title));
    }

    [Fact]
    public void TruncateTitle_VeryLongTitle_TruncatesWithEllipsis()
    {
        var title = new string('A', 100);
        var result = VSCodeReleaseNotesService.TruncateTitle(title);

        Assert.Equal(80, result.Length);
        Assert.EndsWith("...", result);
    }

    // ── GetFirstThursdayOfMonth ───────────────────────────────────────────────

    [Theory]
    [InlineData(2026, 1, 1)]   // Jan 2026: 1st is Thursday
    [InlineData(2026, 2, 5)]   // Feb 2026: 5th is Thursday
    [InlineData(2026, 3, 5)]   // Mar 2026: 5th is Thursday
    public void GetFirstThursdayOfMonth_ReturnsCorrectDate(int year, int month, int expectedDay)
    {
        var result = VSCodeReleaseNotesService.GetFirstThursdayOfMonth(year, month);

        Assert.Equal(new DateOnly(year, month, expectedDay), result);
        Assert.Equal(DayOfWeek.Thursday, result.DayOfWeek);
    }

    // ── GetReleaseMonth ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(2026, 2, 10, 2026, 2)]  // After first Thursday → same month
    [InlineData(2026, 2, 3, 2026, 1)]   // Before first Thursday → previous month
    [InlineData(2026, 2, 5, 2026, 2)]   // On first Thursday → same month
    public void GetReleaseMonth_ReturnsCorrectMonth(int year, int month, int day, int expectedYear, int expectedMonth)
    {
        var result = VSCodeReleaseNotesService.GetReleaseMonth(new DateOnly(year, month, day));

        Assert.Equal(new DateOnly(expectedYear, expectedMonth, 1), result);
    }

    // ── ExtractCategory ───────────────────────────────────────────────────────

    [Fact]
    public void ExtractCategory_WithDash_ExtractsAfterDash()
    {
        var result = VSCodeReleaseNotesService.ExtractCategory("## February 10 - Editor");
        Assert.Equal("Editor", result);
    }

    [Fact]
    public void ExtractCategory_NoDash_ReturnsGeneral()
    {
        var result = VSCodeReleaseNotesService.ExtractCategory("## February 10");
        Assert.Equal("General", result);
    }

    // ── GetProductEdition ────────────────────────────────────────────────────

    [Theory]
    [InlineData("---\nProductEdition: Insiders\n---\n# Content", "Insiders")]
    [InlineData("---\nProductEdition: insiders\n---\n# Content", "insiders")]
    [InlineData("---\nProductEdition: Stable\n---\n# Content", "Stable")]
    [InlineData("# No front matter here", null)]
    [InlineData("---\nTitle: Some Title\n---\n# Content", null)]
    public void GetProductEdition_ReturnsCorrectEdition(string markdown, string? expected)
    {
        Assert.Equal(expected, VSCodeReleaseNotesService.GetProductEdition(markdown));
    }

    // ── ParseStableHighlights ─────────────────────────────────────────────────

    [Fact]
    public void ParseStableHighlights_ExtractsBulletsFromWelcomeSection()
    {
        var markdown = """
            ---
            ProductEdition: Stable
            ---
            # Visual Studio Code 1.116

            ---

            Welcome to the 1.116 release of Visual Studio Code. Here are some highlights:

            * [Agent Debug Logs](#debug-previous-agent-sessions): view logs from previous agent sessions.
            * [Copilot CLI thinking effort](#configure-thinking-effort): configure model thinking effort.

            Happy Coding!

            ---

            ## Agent experience
            """;

        var highlights = VSCodeReleaseNotesService.ParseStableHighlights(markdown);

        Assert.Equal(2, highlights.Count);
        Assert.Equal("Agent Debug Logs: view logs from previous agent sessions.", highlights[0]);
        Assert.Equal("Copilot CLI thinking effort: configure model thinking effort.", highlights[1]);
    }

    [Fact]
    public void ParseStableHighlights_ReturnsEmptyForNoWelcomeSection()
    {
        var markdown = """
            ---
            ProductEdition: Stable
            ---
            # Visual Studio Code 1.116

            ## Agent experience
            """;

        var highlights = VSCodeReleaseNotesService.ParseStableHighlights(markdown);

        Assert.Empty(highlights);
    }
}
