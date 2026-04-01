using Microsoft.Extensions.Logging;
using NewsletterGenerator.Models;
using NewsletterGenerator.Services;
using Spectre.Console;

namespace NewsletterGenerator;

internal static partial class NewsletterApp
{
    private static async Task<(string? Content, string Title)> GenerateVsCodeNewsletterAsync(
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string selectedModel,
        ILoggerFactory loggerFactory,
        RunMetrics metrics,
        bool debug)
    {
        var defaultTitle = "VS Code Insiders Weekly Newsletter";

        var feedService = new AtomFeedService(loggerFactory.CreateLogger<AtomFeedService>(), feedCache: cache);
        var vscodeService = new VSCodeReleaseNotesService();
        VSCodeReleaseNotes? releaseNotes = null;
        List<ReleaseEntry> vscodeBlogEntries = [];
        List<ReleaseEntry> changelogEntries = [];
        List<ReleaseEntry> githubBlogEntries = [];
        VSCodeReleaseNotesFetchResult? releaseNotesResult = null;
        FeedFetchResult? vscodeBlogResult = null;
        FeedFetchResult? changelogResult = null;
        FeedFetchResult? githubBlogResult = null;

        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            const string notesLabel = "VS Code release notes";
            const string vscodeBlogLabel = "VS Code blog feed";
            const string changelogLabel = "Copilot changelog feed";
            const string githubBlogLabel = "GitHub blog feed";

            var notesTask = AddInactiveTask(ctx, notesLabel);
            var vscodeBlogTask = AddInactiveTask(ctx, vscodeBlogLabel);
            var changelogTask = AddInactiveTask(ctx, changelogLabel);
            var githubBlogTask = AddInactiveTask(ctx, githubBlogLabel);

            releaseNotesResult = await RunTrackedTaskAsync(
                notesTask,
                notesLabel,
                () => vscodeService.GetReleaseNotesFetchResultForDateRangeAsync(weekStart, weekEnd),
                metrics,
                "Fetch: VS Code release notes");
            releaseNotes = releaseNotesResult.ReleaseNotes;

            vscodeBlogResult = await RunTrackedTaskAsync(
                vscodeBlogTask,
                vscodeBlogLabel,
                () => feedService.FetchFeedWithMetricsAsync(
                    FeedUrls.VSCodeBlog,
                    weekStart,
                    weekEnd,
                    preferShortSummary: true,
                    maxContentChars: 1000),
                metrics,
                "Fetch: VS Code blog");
            vscodeBlogEntries = vscodeBlogResult.Entries;

            changelogResult = await RunTrackedTaskAsync(
                changelogTask,
                changelogLabel,
                () => feedService.FetchFeedWithMetricsAsync(
                    FeedUrls.ChangelogCopilot,
                    weekStart,
                    weekEnd,
                    maxContentChars: 1500),
                metrics,
                "Fetch: Copilot changelog");
            changelogEntries = changelogResult.Entries;

            githubBlogResult = await RunTrackedTaskAsync(
                githubBlogTask,
                githubBlogLabel,
                () => feedService.FetchFeedWithMetricsAsync(
                    FeedUrls.GitHubBlog,
                    weekStart,
                    weekEnd,
                    preferShortSummary: true,
                    maxContentChars: 1000),
                metrics,
                "Fetch: GitHub blog");
            githubBlogEntries = githubBlogResult.Entries;
        });

        var vscodeMentionEntries = vscodeBlogEntries.Where(MentionsVsCode).ToList();
        var changelogVsCodeEntries = changelogEntries.Where(MentionsVsCode).ToList();
        var githubBlogVsCodeEntries = githubBlogEntries.Where(MentionsVsCode).ToList();

        // Fall back to the feed-provided release page when the markdown parser
        // cannot extract feature bullets from the current release notes file.
        if (releaseNotes == null)
        {
            var fallbackReleaseUrl = vscodeMentionEntries
                .Select(entry => entry.Url)
                .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)
                    && url.Contains("/updates/v1_", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(fallbackReleaseUrl))
            {
                releaseNotes = new VSCodeReleaseNotes(
                    Date: weekEnd,
                    Features: [],
                    VersionUrl: fallbackReleaseUrl);

                metrics.Warnings.Add("VS Code release-note bullets could not be parsed; using the feed release page as a fallback source.");
            }
        }

        metrics.SourceCounts.Add(new SourceCount(
            "VS Code Insiders",
            $"{releaseNotesResult?.CandidateUrlCount ?? 0} files",
            $"{releaseNotesResult?.MatchedSectionCount ?? 0} sections",
            $"{releaseNotesResult?.UniqueFeatureCount ?? 0} features",
            $"{releaseNotesResult?.SuccessfulUrlCount ?? 0} files parsed successfully"));
        metrics.SourceCounts.Add(new SourceCount(
            "VS Code Blog",
            (vscodeBlogResult?.TotalItems ?? 0).ToString(),
            (vscodeBlogResult?.InRangeItems ?? 0).ToString(),
            vscodeMentionEntries.Count.ToString(),
            "Posts mentioning VS Code"));
        metrics.SourceCounts.Add(new SourceCount(
            "GitHub Changelog",
            (changelogResult?.TotalItems ?? 0).ToString(),
            (changelogResult?.InRangeItems ?? 0).ToString(),
            changelogVsCodeEntries.Count.ToString(),
            "Copilot entries mentioning VS Code"));
        metrics.SourceCounts.Add(new SourceCount(
            "GitHub Blog",
            (githubBlogResult?.TotalItems ?? 0).ToString(),
            (githubBlogResult?.InRangeItems ?? 0).ToString(),
            githubBlogVsCodeEntries.Count.ToString(),
            "Posts mentioning VS Code"));

        if (releaseNotes == null &&
            vscodeMentionEntries.Count == 0 &&
            changelogVsCodeEntries.Count == 0 &&
            githubBlogVsCodeEntries.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠[/] No VS Code-related items found in [bold]{weekStart:yyyy-MM-dd}[/] to [bold]{weekEnd:yyyy-MM-dd}[/].");
            return (null, defaultTitle);
        }

        var releaseFeatures = releaseNotes?.Features ?? [];

        var categorySummary = releaseFeatures
            .GroupBy(f => f.Category)
            .OrderByDescending(g => g.Count())
            .Take(4)
            .Select(g => $"{g.Key} ({g.Count()})");

        var vscodeTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Source[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Items[/]").Centered())
            .AddColumn(new TableColumn("[bold]Top categories[/]").LeftAligned());

        var topCategories = string.Join(", ", categorySummary);
        if (string.IsNullOrWhiteSpace(topCategories))
            topCategories = releaseNotes != null
                ? "Release page from VS Code feed"
                : "No release notes";

        vscodeTable.AddRow("[cornflowerblue]VS Code Insiders[/]", $"[green]{releaseFeatures.Count}[/]", topCategories);
        vscodeTable.AddRow("[cornflowerblue]VS Code Blog[/]", $"[green]{vscodeMentionEntries.Count}[/]", "Posts mentioning VS Code");
        vscodeTable.AddRow("[cornflowerblue]GitHub Changelog[/]", $"[green]{changelogVsCodeEntries.Count}[/]", "Copilot changelog items mentioning VS Code");
        vscodeTable.AddRow("[cornflowerblue]GitHub Blog[/]", $"[green]{githubBlogVsCodeEntries.Count}[/]", "Posts mentioning VS Code");

        AnsiConsole.Write(vscodeTable);
        AnsiConsole.WriteLine();

        var newsletterService = new NewsletterService(loggerFactory.CreateLogger<NewsletterService>());
        string content = string.Empty;
        string title = defaultTitle;

        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            const string sectionLabel = "Generate newsletter content";
            const string titleLabel = "Generate title";

            var sectionTask = AddInactiveTask(ctx, sectionLabel);
            var titleTask = AddInactiveTask(ctx, titleLabel);

            content = await RunTrackedTaskAsync(
                sectionTask,
                sectionLabel,
                () => newsletterService.GenerateVsCodeNewsletterAsync(
                    releaseNotes!,
                    vscodeMentionEntries,
                    changelogVsCodeEntries,
                    githubBlogVsCodeEntries,
                    weekStart,
                    weekEnd,
                    cache,
                    selectedModel),
                metrics,
                "Generate: VS Code newsletter");

            var welcomeSummary = ExtractWelcomeSummary(content);
            var newsletterLabel = GetNewsletterLabel(NewsletterType.VSCode);
            title = await RunTrackedTaskAsync(
                titleTask,
                titleLabel,
                () => newsletterService.GenerateNewsletterTitleAsync(
                    welcomeSummary,
                    newsletterLabel,
                    cache,
                    selectedModel),
                metrics,
                "Generate: Newsletter title");
        });

        if (string.IsNullOrWhiteSpace(content))
        {
            AnsiConsole.MarkupLine("[yellow]⚠[/] Empty VS Code newsletter result.");
            return (null, defaultTitle);
        }

        return (content, title);
    }

    private static async Task<(string? Content, string Title)> GenerateCopilotNewsletterAsync(
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string selectedModel,
        ILoggerFactory loggerFactory,
        RunMetrics metrics,
        bool debug)
    {
        var defaultTitle = "GitHub Copilot CLI/SDK Weekly Newsletter";
        var feedService = new AtomFeedService(loggerFactory.CreateLogger<AtomFeedService>(), feedCache: cache);
        var log = loggerFactory.CreateLogger("CopilotNewsletter");

        List<ReleaseEntry> cliReleases = [];
        List<ReleaseEntry> sdkReleases = [];
        List<ReleaseEntry> changelogEntries = [];
        List<ReleaseEntry> blogEntries = [];
        FeedFetchResult? cliFetchResult = null;
        FeedFetchResult? sdkFetchResult = null;
        FeedFetchResult? changelogFetchResult = null;
        FeedFetchResult? blogFetchResult = null;

        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            const string cliLabel = "Copilot CLI releases";
            const string sdkLabel = "Copilot SDK releases";
            const string changelogLabel = "Copilot changelog feed";
            const string blogLabel = "GitHub blog feed";

            var cliTask = AddInactiveTask(ctx, cliLabel);
            var sdkTask = AddInactiveTask(ctx, sdkLabel);
            var changelogTask = AddInactiveTask(ctx, changelogLabel);
            var blogTask = AddInactiveTask(ctx, blogLabel);

            cliFetchResult = await RunTrackedTaskAsync(
                cliTask,
                cliLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.CliAtom, weekStart, weekEnd),
                metrics,
                "Fetch: Copilot CLI releases");
            cliReleases = cliFetchResult.Entries;

            sdkFetchResult = await RunTrackedTaskAsync(
                sdkTask,
                sdkLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.SdkAtom, weekStart, weekEnd),
                metrics,
                "Fetch: Copilot SDK releases");
            sdkReleases = sdkFetchResult.Entries;

            changelogFetchResult = await RunTrackedTaskAsync(
                changelogTask,
                changelogLabel,
                () => feedService.FetchFeedWithMetricsAsync(
                    FeedUrls.ChangelogCopilot,
                    weekStart,
                    weekEnd,
                    maxContentChars: 1500),
                metrics,
                "Fetch: Copilot changelog");
            changelogEntries = changelogFetchResult.Entries;

            blogFetchResult = await RunTrackedTaskAsync(
                blogTask,
                blogLabel,
                () => feedService.FetchFeedWithMetricsAsync(
                    FeedUrls.GitHubBlog,
                    weekStart,
                    weekEnd,
                    categoryKeywords: ["copilot", "github copilot cli", "github cli"],
                    preferShortSummary: true,
                    maxContentChars: 800),
                metrics,
                "Fetch: GitHub blog");
            blogEntries = blogFetchResult.Entries;
        });

        var cliPreCount = cliReleases.Count;
        var sdkPreCount = sdkReleases.Count;
        var cliConsolidation = AtomFeedService.ConsolidatePrereleasesWithMetrics(cliReleases);
        var sdkConsolidation = AtomFeedService.ConsolidatePrereleasesWithMetrics(sdkReleases);
        cliReleases = cliConsolidation.Releases;
        sdkReleases = sdkConsolidation.Releases;

        metrics.PrereleaseCounts.Add(new PrereleaseCount(
            "Copilot CLI releases",
            cliFetchResult?.MatchedItems ?? 0,
            cliReleases.Select(r => r.Version).ToList(),
            cliConsolidation.RolledUpPrereleases,
            cliConsolidation.SkippedPrereleases));
        metrics.PrereleaseCounts.Add(new PrereleaseCount(
            "Copilot SDK releases",
            sdkFetchResult?.MatchedItems ?? 0,
            sdkReleases.Select(r => r.Version).ToList(),
            sdkConsolidation.RolledUpPrereleases,
            sdkConsolidation.SkippedPrereleases));

        log.LogInformation("ConsolidatePrereleases: CLI {Before}->{After}, SDK {SdkBefore}->{SdkAfter}",
            cliPreCount, cliReleases.Count, sdkPreCount, sdkReleases.Count);

        metrics.SourceCounts.Add(new SourceCount(
            "Copilot CLI releases",
            (cliFetchResult?.TotalItems ?? 0).ToString(),
            (cliFetchResult?.InRangeItems ?? 0).ToString(),
            cliReleases.Count.ToString(),
            $"{cliFetchResult?.MatchedItems ?? 0} matched; prereleases {cliConsolidation.DetectedPrereleases} ({cliConsolidation.RolledUpCount} rolled up, {cliConsolidation.SkippedCount} skipped)"));
        metrics.SourceCounts.Add(new SourceCount(
            "Copilot SDK releases",
            (sdkFetchResult?.TotalItems ?? 0).ToString(),
            (sdkFetchResult?.InRangeItems ?? 0).ToString(),
            sdkReleases.Count.ToString(),
            $"{sdkFetchResult?.MatchedItems ?? 0} matched; prereleases {sdkConsolidation.DetectedPrereleases} ({sdkConsolidation.RolledUpCount} rolled up, {sdkConsolidation.SkippedCount} skipped)"));
        metrics.SourceCounts.Add(new SourceCount(
            "Changelog (Copilot)",
            (changelogFetchResult?.TotalItems ?? 0).ToString(),
            (changelogFetchResult?.InRangeItems ?? 0).ToString(),
            changelogEntries.Count.ToString(),
            "Feed items"));
        metrics.SourceCounts.Add(new SourceCount(
            "Blog (Copilot/CLI)",
            (blogFetchResult?.TotalItems ?? 0).ToString(),
            (blogFetchResult?.InRangeItems ?? 0).ToString(),
            blogEntries.Count.ToString(),
            "Filtered by category"));

        static string CountCell(int n) => n == 0 ? "[dim]0[/]" : $"[green]{n}[/]";
        static string ItemsCell(IEnumerable<ReleaseEntry> entries, int max = 3)
        {
            var titles = entries.Take(max)
                .Select(e => $"[white]{Markup.Escape(e.Version.Length > 40 ? e.Version[..40] + "..." : e.Version)}[/]");
            var list = string.Join(", ", titles);
            return list.Length == 0 ? "[dim]none[/]" : list;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Source[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Items[/]").Centered())
            .AddColumn(new TableColumn("[bold]Recent entries[/]").LeftAligned());

        table.AddRow("[cornflowerblue]Copilot CLI releases[/]", CountCell(cliReleases.Count), ItemsCell(cliReleases));
        table.AddRow("[cornflowerblue]Copilot SDK releases[/]", CountCell(sdkReleases.Count), ItemsCell(sdkReleases));
        table.AddRow("[cornflowerblue]Changelog (Copilot)[/]", CountCell(changelogEntries.Count), ItemsCell(changelogEntries));
        table.AddRow("[cornflowerblue]Blog (Copilot/CLI)[/]", CountCell(blogEntries.Count), ItemsCell(blogEntries));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (cliReleases.Count == 0 && sdkReleases.Count == 0 && changelogEntries.Count == 0 && blogEntries.Count == 0)
        {
            log.LogWarning("No items found for date range {Start} to {End}", weekStart, weekEnd);
            AnsiConsole.MarkupLine($"[yellow]⚠[/] No items found in [bold]{weekStart:yyyy-MM-dd}[/] to [bold]{weekEnd:yyyy-MM-dd}[/].");
            return (null, defaultTitle);
        }

        static string ExtractTLDRBullets(string releaseSection)
        {
            var lines = releaseSection.Split('\n');
            var bullets = new StringBuilder();
            bool inTLDR = false;

            foreach (var line in lines)
            {
                if (line.Contains("### GitHub Copilot CLI") || line.Contains("### GitHub Copilot SDK"))
                {
                    inTLDR = true;
                    continue;
                }

                if (line.StartsWith("## Releases") || line.StartsWith("---"))
                    inTLDR = false;

                if (inTLDR && line.TrimStart().StartsWith("-"))
                    bullets.AppendLine(line);
            }

            return bullets.ToString();
        }

        string newsSection = string.Empty;
        string releaseSection = string.Empty;
        string welcomeSummary = string.Empty;

        var newsletterService = new NewsletterService(loggerFactory.CreateLogger<NewsletterService>());
        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            const string newsLabel = "News and announcements";
            const string releaseLabel = "Project updates";
            const string welcomeLabel = "Welcome summary";
            const string titleLabel = "Newsletter title";

            var newsTask = AddInactiveTask(ctx, newsLabel);
            var releaseTask = AddInactiveTask(ctx, releaseLabel);
            var welcomeTask = AddInactiveTask(ctx, welcomeLabel);
            var titleTask = AddInactiveTask(ctx, titleLabel);

            try
            {
                var newsSectionTask = (changelogEntries.Count > 0 || blogEntries.Count > 0)
                    ? RunTrackedTaskAsync(
                        newsTask,
                        newsLabel,
                        () => newsletterService.GenerateNewsAndAnnouncementsAsync(
                        changelogEntries,
                        blogEntries,
                        weekStart,
                        weekEnd,
                        cache,
                        selectedModel),
                        metrics,
                        "Generate: News and announcements")
                    : Task.FromResult(string.Empty);

                var releaseSectionTask = RunTrackedTaskAsync(
                    releaseTask,
                    releaseLabel,
                    () => newsletterService.GenerateReleaseSectionAsync(
                    cliReleases,
                    sdkReleases,
                    weekStart,
                    weekEnd,
                    cache,
                    selectedModel),
                    metrics,
                    "Generate: Project updates");

                await Task.WhenAll(newsSectionTask, releaseSectionTask);

                newsSection = await newsSectionTask;

                releaseSection = await releaseSectionTask;

                var releaseSummaryBullets = ExtractTLDRBullets(releaseSection);
                welcomeSummary = await RunTrackedTaskAsync(
                    welcomeTask,
                    welcomeLabel,
                    () => newsletterService.GenerateWelcomeSummaryAsync(
                        newsSection,
                        releaseSummaryBullets,
                        weekStart,
                        weekEnd,
                        cache,
                        selectedModel),
                    metrics,
                    "Generate: Welcome summary");

                var newsletterLabel = GetNewsletterLabel(NewsletterType.CopilotCliSdk);
                defaultTitle = await RunTrackedTaskAsync(
                    titleTask,
                    titleLabel,
                    () => newsletterService.GenerateNewsletterTitleAsync(
                        welcomeSummary,
                        newsletterLabel,
                        cache,
                        selectedModel),
                    metrics,
                    "Generate: Newsletter title");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error generating newsletter sections");
                RenderFriendlyException(ex, debug);
            }
        });

        if (string.IsNullOrEmpty(releaseSection))
        {
            log.LogWarning("releaseSection is empty, returning null");
            return (null, defaultTitle);
        }

        var contentBuilder = new StringBuilder();
        contentBuilder.AppendLine("Welcome");
        contentBuilder.AppendLine("--------");
        contentBuilder.AppendLine();
        contentBuilder.AppendLine("This is your weekly update for GitHub Copilot CLI & SDK! Feel free to forward internally and encourage your co-workers to subscribe at [https://aka.ms/copilot-cli-insiders/join](https://aka.ms/copilot-cli-insiders/join) and forward this newsletter around!");
        contentBuilder.AppendLine();
        contentBuilder.AppendLine(welcomeSummary);
        contentBuilder.AppendLine();
        contentBuilder.AppendLine("* * * * *");
        contentBuilder.AppendLine();

        if (!string.IsNullOrEmpty(newsSection))
        {
            contentBuilder.AppendLine(newsSection);
            contentBuilder.AppendLine();
            contentBuilder.AppendLine("* * * * *");
            contentBuilder.AppendLine();
        }

        contentBuilder.Append(releaseSection);
        return (contentBuilder.ToString(), defaultTitle);
    }

    private static async Task<(string? Content, string Title)> GenerateDevTechNewsletterAsync(
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string selectedModel,
        ILoggerFactory loggerFactory,
        RunMetrics metrics,
        bool debug)
    {
        var defaultTitle = "DevTech MVP Weekly Newsletter";
        var feedService = new AtomFeedService(loggerFactory.CreateLogger<AtomFeedService>(), feedCache: cache);
        var vscodeService = new VSCodeReleaseNotesService();
        var log = loggerFactory.CreateLogger("DevTechNewsletter");

        VSCodeReleaseNotes? vscodeReleaseNotes = null;
        List<ReleaseEntry> cliReleases = [];
        List<ReleaseEntry> sdkReleases = [];
        List<ReleaseEntry> changelogEntries = [];
        List<ReleaseEntry> vscodeBlogEntries = [];
        List<ReleaseEntry> dotNetBlogEntries = [];
        List<ReleaseEntry> devBlogEntries = [];
        List<ReleaseEntry> vsBlogEntries = [];
        List<ReleaseEntry> azureBlogEntries = [];
        List<ReleaseEntry> aspireBlogEntries = [];
        List<ReleaseEntry> typeScriptBlogEntries = [];
        List<ReleaseEntry> githubBlogEntries = [];
        List<ReleaseEntry> youtubeDotNetEntries = [];

        FeedFetchResult? cliFetchResult = null;
        FeedFetchResult? sdkFetchResult = null;
        FeedFetchResult? changelogFetchResult = null;
        FeedFetchResult? vscodeBlogResult = null;
        FeedFetchResult? dotNetBlogResult = null;
        FeedFetchResult? devBlogResult = null;
        FeedFetchResult? vsBlogResult = null;
        FeedFetchResult? azureBlogResult = null;
        FeedFetchResult? aspireBlogResult = null;
        FeedFetchResult? typeScriptBlogResult = null;
        FeedFetchResult? githubBlogResult = null;
        FeedFetchResult? youtubeDotNetResult = null;
        VSCodeReleaseNotesFetchResult? vscodeNotesResult = null;

        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            const string cliLabel = "Copilot CLI releases";
            const string sdkLabel = "Copilot SDK releases";
            const string changelogLabel = "Copilot changelog";
            const string vscodeNotesLabel = "VS Code release notes";
            const string vscodeBlogLabel = "VS Code blog";
            const string dotNetLabel = ".NET blog";
            const string devBlogLabel = "Developer blog";
            const string vsBlogLabel = "Visual Studio blog";
            const string azureLabel = "Azure blog";
            const string aspireLabel = "Aspire blog";
            const string tsLabel = "TypeScript blog";
            const string githubBlogLabel = "GitHub blog";
            const string youtubeDotNetLabel = "YouTube .NET";

            var cliTask = AddInactiveTask(ctx, cliLabel);
            var sdkTask = AddInactiveTask(ctx, sdkLabel);
            var changelogTask = AddInactiveTask(ctx, changelogLabel);
            var vscodeNotesTask = AddInactiveTask(ctx, vscodeNotesLabel);
            var vscodeBlogTask = AddInactiveTask(ctx, vscodeBlogLabel);
            var dotNetTask = AddInactiveTask(ctx, dotNetLabel);
            var devBlogTask = AddInactiveTask(ctx, devBlogLabel);
            var vsBlogTask = AddInactiveTask(ctx, vsBlogLabel);
            var azureTask = AddInactiveTask(ctx, azureLabel);
            var aspireTask = AddInactiveTask(ctx, aspireLabel);
            var tsTask = AddInactiveTask(ctx, tsLabel);
            var githubBlogTask = AddInactiveTask(ctx, githubBlogLabel);
            var youtubeDotNetTask = AddInactiveTask(ctx, youtubeDotNetLabel);

            // Fetch all feeds - CLI/SDK releases + blog feeds
            cliFetchResult = await RunTrackedTaskAsync(
                cliTask, cliLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.CliAtom, weekStart, weekEnd),
                metrics, "Fetch: Copilot CLI releases");
            cliReleases = cliFetchResult.Entries;

            sdkFetchResult = await RunTrackedTaskAsync(
                sdkTask, sdkLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.SdkAtom, weekStart, weekEnd),
                metrics, "Fetch: Copilot SDK releases");
            sdkReleases = sdkFetchResult.Entries;

            changelogFetchResult = await RunTrackedTaskAsync(
                changelogTask, changelogLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.ChangelogCopilot, weekStart, weekEnd, maxContentChars: 1500),
                metrics, "Fetch: Copilot changelog");
            changelogEntries = changelogFetchResult.Entries;

            vscodeNotesResult = await RunTrackedTaskAsync(
                vscodeNotesTask, vscodeNotesLabel,
                () => vscodeService.GetReleaseNotesFetchResultForDateRangeAsync(weekStart, weekEnd),
                metrics, "Fetch: VS Code release notes");
            vscodeReleaseNotes = vscodeNotesResult.ReleaseNotes;

            vscodeBlogResult = await RunTrackedTaskAsync(
                vscodeBlogTask, vscodeBlogLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.VSCodeBlog, weekStart, weekEnd, preferShortSummary: true, maxContentChars: 800),
                metrics, "Fetch: VS Code blog");
            vscodeBlogEntries = vscodeBlogResult.Entries;

            dotNetBlogResult = await RunTrackedTaskAsync(
                dotNetTask, dotNetLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.DotNetBlog, weekStart, weekEnd, preferShortSummary: true, maxContentChars: 800),
                metrics, "Fetch: .NET blog");
            dotNetBlogEntries = dotNetBlogResult.Entries;

            devBlogResult = await RunTrackedTaskAsync(
                devBlogTask, devBlogLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.DevBlog, weekStart, weekEnd, preferShortSummary: true, maxContentChars: 800),
                metrics, "Fetch: Developer blog");
            devBlogEntries = devBlogResult.Entries;

            vsBlogResult = await RunTrackedTaskAsync(
                vsBlogTask, vsBlogLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.VSBlog, weekStart, weekEnd, preferShortSummary: true, maxContentChars: 800),
                metrics, "Fetch: Visual Studio blog");
            vsBlogEntries = vsBlogResult.Entries;

            azureBlogResult = await RunTrackedTaskAsync(
                azureTask, azureLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.AzureBlog, weekStart, weekEnd, preferShortSummary: true, maxContentChars: 800),
                metrics, "Fetch: Azure blog");
            azureBlogEntries = azureBlogResult.Entries;

            aspireBlogResult = await RunTrackedTaskAsync(
                aspireTask, aspireLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.AspireBlog, weekStart, weekEnd, preferShortSummary: true, maxContentChars: 800),
                metrics, "Fetch: Aspire blog");
            aspireBlogEntries = aspireBlogResult.Entries;

            typeScriptBlogResult = await RunTrackedTaskAsync(
                tsTask, tsLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.TypeScriptBlog, weekStart, weekEnd, preferShortSummary: true, maxContentChars: 800),
                metrics, "Fetch: TypeScript blog");
            typeScriptBlogEntries = typeScriptBlogResult.Entries;

            githubBlogResult = await RunTrackedTaskAsync(
                githubBlogTask, githubBlogLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.GitHubBlog, weekStart, weekEnd, preferShortSummary: true, maxContentChars: 800),
                metrics, "Fetch: GitHub blog");
            githubBlogEntries = githubBlogResult.Entries;

            youtubeDotNetResult = await RunTrackedTaskAsync(
                youtubeDotNetTask, youtubeDotNetLabel,
                () => feedService.FetchFeedWithMetricsAsync(FeedUrls.YouTubeDotNet, weekStart, weekEnd, preferShortSummary: true, maxContentChars: 500),
                metrics, "Fetch: YouTube .NET");
            youtubeDotNetEntries = youtubeDotNetResult.Entries;
        });

        // Consolidate CLI/SDK prereleases
        var cliConsolidation = AtomFeedService.ConsolidatePrereleasesWithMetrics(cliReleases);
        var sdkConsolidation = AtomFeedService.ConsolidatePrereleasesWithMetrics(sdkReleases);
        cliReleases = cliConsolidation.Releases;
        sdkReleases = sdkConsolidation.Releases;

        metrics.SourceCounts.Add(new SourceCount("Copilot CLI", (cliFetchResult?.TotalItems ?? 0).ToString(), (cliFetchResult?.InRangeItems ?? 0).ToString(), cliReleases.Count.ToString(), "Releases"));
        metrics.SourceCounts.Add(new SourceCount("Copilot SDK", (sdkFetchResult?.TotalItems ?? 0).ToString(), (sdkFetchResult?.InRangeItems ?? 0).ToString(), sdkReleases.Count.ToString(), "Releases"));
        metrics.SourceCounts.Add(new SourceCount("Copilot Changelog", (changelogFetchResult?.TotalItems ?? 0).ToString(), (changelogFetchResult?.InRangeItems ?? 0).ToString(), changelogEntries.Count.ToString(), "Feed items"));
        metrics.SourceCounts.Add(new SourceCount("VS Code Insiders", (vscodeNotesResult?.CandidateUrlCount ?? 0).ToString(), (vscodeNotesResult?.MatchedSectionCount ?? 0).ToString(), (vscodeNotesResult?.UniqueFeatureCount ?? 0).ToString(), "Release note features"));
        metrics.SourceCounts.Add(new SourceCount("VS Code Blog", (vscodeBlogResult?.TotalItems ?? 0).ToString(), (vscodeBlogResult?.InRangeItems ?? 0).ToString(), vscodeBlogEntries.Count.ToString(), "Blog posts"));
        metrics.SourceCounts.Add(new SourceCount(".NET Blog", (dotNetBlogResult?.TotalItems ?? 0).ToString(), (dotNetBlogResult?.InRangeItems ?? 0).ToString(), dotNetBlogEntries.Count.ToString(), "Blog posts"));
        metrics.SourceCounts.Add(new SourceCount("Developer Blog", (devBlogResult?.TotalItems ?? 0).ToString(), (devBlogResult?.InRangeItems ?? 0).ToString(), devBlogEntries.Count.ToString(), "Blog posts"));
        metrics.SourceCounts.Add(new SourceCount("VS Blog", (vsBlogResult?.TotalItems ?? 0).ToString(), (vsBlogResult?.InRangeItems ?? 0).ToString(), vsBlogEntries.Count.ToString(), "Blog posts"));
        metrics.SourceCounts.Add(new SourceCount("Azure Blog", (azureBlogResult?.TotalItems ?? 0).ToString(), (azureBlogResult?.InRangeItems ?? 0).ToString(), azureBlogEntries.Count.ToString(), "Blog posts"));
        metrics.SourceCounts.Add(new SourceCount("Aspire Blog", (aspireBlogResult?.TotalItems ?? 0).ToString(), (aspireBlogResult?.InRangeItems ?? 0).ToString(), aspireBlogEntries.Count.ToString(), "Blog posts"));
        metrics.SourceCounts.Add(new SourceCount("TypeScript Blog", (typeScriptBlogResult?.TotalItems ?? 0).ToString(), (typeScriptBlogResult?.InRangeItems ?? 0).ToString(), typeScriptBlogEntries.Count.ToString(), "Blog posts"));
        metrics.SourceCounts.Add(new SourceCount("GitHub Blog", (githubBlogResult?.TotalItems ?? 0).ToString(), (githubBlogResult?.InRangeItems ?? 0).ToString(), githubBlogEntries.Count.ToString(), "Blog posts"));
        metrics.SourceCounts.Add(new SourceCount("YouTube .NET", (youtubeDotNetResult?.TotalItems ?? 0).ToString(), (youtubeDotNetResult?.InRangeItems ?? 0).ToString(), youtubeDotNetEntries.Count.ToString(), "Videos"));

        var vscodeFeatureCount = vscodeReleaseNotes?.Features.Count ?? 0;
        var totalItems = cliReleases.Count + sdkReleases.Count + changelogEntries.Count +
            vscodeFeatureCount + vscodeBlogEntries.Count + dotNetBlogEntries.Count + devBlogEntries.Count +
            vsBlogEntries.Count + azureBlogEntries.Count + aspireBlogEntries.Count +
            typeScriptBlogEntries.Count + githubBlogEntries.Count + youtubeDotNetEntries.Count;

        if (totalItems == 0)
        {
            log.LogWarning("No items found for date range {Start} to {End}", weekStart, weekEnd);
            AnsiConsole.MarkupLine($"[yellow]⚠[/] No items found in [bold]{weekStart:yyyy-MM-dd}[/] to [bold]{weekEnd:yyyy-MM-dd}[/].");
            return (null, defaultTitle);
        }

        static string CountCell(int n) => n == 0 ? "[dim]0[/]" : $"[green]{n}[/]";

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Source[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Items[/]").Centered());

        table.AddRow("[cornflowerblue]Copilot CLI releases[/]", CountCell(cliReleases.Count));
        table.AddRow("[cornflowerblue]Copilot SDK releases[/]", CountCell(sdkReleases.Count));
        table.AddRow("[cornflowerblue]Copilot Changelog[/]", CountCell(changelogEntries.Count));
        table.AddRow("[cornflowerblue]VS Code Insiders[/]", CountCell(vscodeFeatureCount));
        table.AddRow("[cornflowerblue]VS Code Blog[/]", CountCell(vscodeBlogEntries.Count));
        table.AddRow("[cornflowerblue].NET Blog[/]", CountCell(dotNetBlogEntries.Count));
        table.AddRow("[cornflowerblue]Developer Blog[/]", CountCell(devBlogEntries.Count));
        table.AddRow("[cornflowerblue]Visual Studio Blog[/]", CountCell(vsBlogEntries.Count));
        table.AddRow("[cornflowerblue]Azure Blog[/]", CountCell(azureBlogEntries.Count));
        table.AddRow("[cornflowerblue]Aspire Blog[/]", CountCell(aspireBlogEntries.Count));
        table.AddRow("[cornflowerblue]TypeScript Blog[/]", CountCell(typeScriptBlogEntries.Count));
        table.AddRow("[cornflowerblue]GitHub Blog[/]", CountCell(githubBlogEntries.Count));
        table.AddRow("[cornflowerblue]YouTube .NET[/]", CountCell(youtubeDotNetEntries.Count));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var newsletterService = new NewsletterService(loggerFactory.CreateLogger<NewsletterService>());
        string content = string.Empty;
        string title = defaultTitle;

        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            const string sectionLabel = "Generate newsletter content";
            const string titleLabel = "Generate title";

            var sectionTask = AddInactiveTask(ctx, sectionLabel);
            var titleTask = AddInactiveTask(ctx, titleLabel);

            content = await RunTrackedTaskAsync(
                sectionTask,
                sectionLabel,
                () => newsletterService.GenerateDevTechNewsletterAsync(
                    cliReleases, sdkReleases, changelogEntries,
                    vscodeBlogEntries, vscodeReleaseNotes,
                    dotNetBlogEntries, devBlogEntries,
                    vsBlogEntries, azureBlogEntries, aspireBlogEntries,
                    typeScriptBlogEntries,
                    githubBlogEntries, youtubeDotNetEntries,
                    FeedUrls.VSReleaseNotes, FeedUrls.VSInsidersReleaseNotes,
                    weekStart, weekEnd, cache, selectedModel),
                metrics,
                "Generate: DevTech newsletter");

            var welcomeSummary = ExtractWelcomeSummary(content);
            var newsletterLabel = GetNewsletterLabel(NewsletterType.DevTechMVP);
            title = await RunTrackedTaskAsync(
                titleTask,
                titleLabel,
                () => newsletterService.GenerateNewsletterTitleAsync(
                    welcomeSummary,
                    newsletterLabel,
                    cache,
                    selectedModel),
                metrics,
                "Generate: Newsletter title");
        });

        if (string.IsNullOrWhiteSpace(content))
        {
            AnsiConsole.MarkupLine("[yellow]⚠[/] Empty DevTech newsletter result.");
            return (null, defaultTitle);
        }

        return (content, title);
    }

    private static bool MentionsVsCode(ReleaseEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Version) && string.IsNullOrWhiteSpace(entry.PlainText))
            return false;

        var combined = $"{entry.Version}\n{entry.PlainText}";
        return combined.Contains("vs code", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("vscode", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("visual studio code", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("insiders", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("code.visualstudio.com", StringComparison.OrdinalIgnoreCase);
    }
}
