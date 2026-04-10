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
        var defaultTitle = "VS Code Weekly Newsletter";

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

        (cliReleases, var cliConsolidation) = ConsolidateAndTrack("Copilot CLI releases", cliReleases, cliFetchResult, metrics);
        (sdkReleases, var sdkConsolidation) = ConsolidateAndTrack("Copilot SDK releases", sdkReleases, sdkFetchResult, metrics);

        log.LogInformation("ConsolidatePrereleases: CLI -> {CliAfter}, SDK -> {SdkAfter}",
            cliReleases.Count, sdkReleases.Count);

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

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Source[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Items[/]").Centered())
            .AddColumn(new TableColumn("[bold]Recent entries[/]").LeftAligned());

        table.AddRow("[cornflowerblue]Copilot CLI releases[/]", FormatCountCell(cliReleases.Count), FormatItemsCell(cliReleases));
        table.AddRow("[cornflowerblue]Copilot SDK releases[/]", FormatCountCell(sdkReleases.Count), FormatItemsCell(sdkReleases));
        table.AddRow("[cornflowerblue]Changelog (Copilot)[/]", FormatCountCell(changelogEntries.Count), FormatItemsCell(changelogEntries));
        table.AddRow("[cornflowerblue]Blog (Copilot/CLI)[/]", FormatCountCell(blogEntries.Count), FormatItemsCell(blogEntries));

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

        // Define all feed sources as descriptors
        var feedDescriptors = new (string Key, string Label, string Url, bool PreferShortSummary, int MaxContentChars, string Notes)[]
        {
            ("cli",         "Copilot CLI releases",  FeedUrls.CliAtom,              false, 0,    "Releases"),
            ("sdk",         "Copilot SDK releases",  FeedUrls.SdkAtom,              false, 0,    "Releases"),
            ("changelog",   "Copilot changelog",     FeedUrls.ChangelogCopilot,     false, 1500, "Feed items"),
            ("vscodeBlog",  "VS Code blog",          FeedUrls.VSCodeBlog,           true,  800,  "Blog posts"),
            ("dotNet",      ".NET blog",             FeedUrls.DotNetBlog,           true,  800,  "Blog posts"),
            ("devBlog",     "Developer blog",        FeedUrls.DevBlog,              true,  800,  "Blog posts"),
            ("vsBlog",      "Visual Studio blog",    FeedUrls.VSBlog,               true,  800,  "Blog posts"),
            ("azure",       "Azure blog",            FeedUrls.AzureBlog,            true,  800,  "Blog posts"),
            ("aspire",      "Aspire blog",           FeedUrls.AspireBlog,           true,  800,  "Blog posts"),
            ("typeScript",  "TypeScript blog",       FeedUrls.TypeScriptBlog,       true,  800,  "Blog posts"),
            ("githubBlog",  "GitHub blog",           FeedUrls.GitHubBlog,           true,  800,  "Blog posts"),
            ("ytDotNet",    "YouTube .NET",          FeedUrls.YouTubeDotNet,        true,  500,  "Videos"),
            ("ytVS",        "YouTube Visual Studio", FeedUrls.YouTubeVisualStudio,  true,  500,  "Videos"),
            ("ytVSCode",    "YouTube VS Code",       FeedUrls.YouTubeVSCode,        true,  500,  "Videos"),
            ("ytGitHub",    "YouTube GitHub",        FeedUrls.YouTubeGitHub,        true,  500,  "Videos"),
            ("ytMSDev",     "YouTube Microsoft Dev", FeedUrls.YouTubeMicrosoftDev,  true,  500,  "Videos"),
        };

        var feedResults = new Dictionary<string, FeedFetchResult>(StringComparer.OrdinalIgnoreCase);
        VSCodeReleaseNotes? vscodeReleaseNotes = null;
        VSCodeReleaseNotesFetchResult? vscodeNotesResult = null;

        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            // VS Code release notes (non-feed source)
            const string vscodeNotesLabel = "VS Code release notes";
            var vscodeNotesTask = AddInactiveTask(ctx, vscodeNotesLabel);

            // Create progress tasks for all feeds
            var feedTasks = feedDescriptors.Select(d => (Descriptor: d, ProgressTask: AddInactiveTask(ctx, d.Label))).ToList();

            // Fetch VS Code release notes
            vscodeNotesResult = await RunTrackedTaskAsync(
                vscodeNotesTask, vscodeNotesLabel,
                () => vscodeService.GetReleaseNotesFetchResultForDateRangeAsync(weekStart, weekEnd),
                metrics, "Fetch: VS Code release notes");
            vscodeReleaseNotes = vscodeNotesResult.ReleaseNotes;

            // Fetch all feeds
            foreach (var (descriptor, progressTask) in feedTasks)
            {
                var result = await RunTrackedTaskAsync(
                    progressTask, descriptor.Label,
                    () => feedService.FetchFeedWithMetricsAsync(
                        descriptor.Url, weekStart, weekEnd,
                        preferShortSummary: descriptor.PreferShortSummary,
                        maxContentChars: descriptor.MaxContentChars),
                    metrics, $"Fetch: {descriptor.Label}");
                feedResults[descriptor.Key] = result;
            }
        });

        List<ReleaseEntry> Entries(string key) => feedResults.TryGetValue(key, out var r) ? r.Entries : [];

        var cliReleases = Entries("cli");
        var sdkReleases = Entries("sdk");
        var changelogEntries = Entries("changelog");
        var vscodeBlogEntries = Entries("vscodeBlog");
        var dotNetBlogEntries = Entries("dotNet");
        var devBlogEntries = Entries("devBlog");
        var vsBlogEntries = Entries("vsBlog");
        var azureBlogEntries = Entries("azure");
        var aspireBlogEntries = Entries("aspire");
        var typeScriptBlogEntries = Entries("typeScript");
        var githubBlogEntries = Entries("githubBlog");
        var youtubeDotNetEntries = Entries("ytDotNet");
        var youtubeVSEntries = Entries("ytVS");
        var youtubeVSCodeEntries = Entries("ytVSCode");
        var youtubeGitHubEntries = Entries("ytGitHub");
        var youtubeMicrosoftDevEntries = Entries("ytMSDev");

        // Consolidate CLI/SDK prereleases
        (cliReleases, _) = ConsolidateAndTrack("Copilot CLI", cliReleases, feedResults.GetValueOrDefault("cli"), metrics);
        (sdkReleases, _) = ConsolidateAndTrack("Copilot SDK", sdkReleases, feedResults.GetValueOrDefault("sdk"), metrics);

        // Track source counts and build summary table
        foreach (var descriptor in feedDescriptors)
        {
            var result = feedResults.GetValueOrDefault(descriptor.Key);
            var entries = descriptor.Key is "cli" ? cliReleases : descriptor.Key is "sdk" ? sdkReleases : Entries(descriptor.Key);
            metrics.SourceCounts.Add(new SourceCount(
                descriptor.Label,
                (result?.TotalItems ?? 0).ToString(),
                (result?.InRangeItems ?? 0).ToString(),
                entries.Count.ToString(),
                descriptor.Notes));
        }
        metrics.SourceCounts.Add(new SourceCount(
            "VS Code Insiders",
            (vscodeNotesResult?.CandidateUrlCount ?? 0).ToString(),
            (vscodeNotesResult?.MatchedSectionCount ?? 0).ToString(),
            (vscodeNotesResult?.UniqueFeatureCount ?? 0).ToString(),
            "Release note features"));

        var vscodeFeatureCount = vscodeReleaseNotes?.Features.Count ?? 0;
        var totalItems = feedResults.Values.Sum(r => r.Entries.Count) + vscodeFeatureCount;

        if (totalItems == 0)
        {
            log.LogWarning("No items found for date range {Start} to {End}", weekStart, weekEnd);
            AnsiConsole.MarkupLine($"[yellow]⚠[/] No items found in [bold]{weekStart:yyyy-MM-dd}[/] to [bold]{weekEnd:yyyy-MM-dd}[/].");
            return (null, defaultTitle);
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Source[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Items[/]").Centered());

        foreach (var descriptor in feedDescriptors)
        {
            var entries = descriptor.Key is "cli" ? cliReleases : descriptor.Key is "sdk" ? sdkReleases : Entries(descriptor.Key);
            table.AddRow($"[cornflowerblue]{Markup.Escape(descriptor.Label)}[/]", FormatCountCell(entries.Count));
        }
        table.AddRow("[cornflowerblue]VS Code Insiders[/]", FormatCountCell(vscodeFeatureCount));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var newsletterService = new NewsletterService(loggerFactory.CreateLogger<NewsletterService>());

        // Detect major releases from blog pool
        List<ReleaseEntry> blogPool = [..dotNetBlogEntries, ..devBlogEntries, ..azureBlogEntries,
            ..aspireBlogEntries, ..typeScriptBlogEntries, ..githubBlogEntries];
        var majorReleases = NewsletterService.DetectMajorReleases(blogPool);
        var majorReleaseTitles = majorReleases.Select(e => e.Version).ToList();

        if (majorReleases.Count > 0)
        {
            AnsiConsole.MarkupLine($"[cornflowerblue]Detected {majorReleases.Count} major release(s):[/]");
            foreach (var mr in majorReleases)
                AnsiConsole.MarkupLine($"  [white]{Markup.Escape(mr.Version)}[/]");
            AnsiConsole.WriteLine();
        }

        string copilotSection = string.Empty;
        string vscodeSection = string.Empty;
        string vsSection = string.Empty;
        List<string> majorReleaseSections = [];
        string blogsSection = string.Empty;
        string videosSection = string.Empty;
        string welcomeSection = string.Empty;
        string content = string.Empty;
        string title = defaultTitle;

        // Phase 1: Generate all body sections in parallel
        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            const string copilotLabel = "Copilot CLI & SDK section";
            const string vscodeLabel = "VS Code section";
            const string vsLabel = "Visual Studio section";
            const string blogsLabel = "Developer Blogs section";
            const string videosLabel = "Developer Videos section";

            var copilotTask = AddInactiveTask(ctx, copilotLabel);
            var vscodeTask = AddInactiveTask(ctx, vscodeLabel);
            var vsTask = AddInactiveTask(ctx, vsLabel);
            var blogsTask = AddInactiveTask(ctx, blogsLabel);
            var videosTask = AddInactiveTask(ctx, videosLabel);

            var majorTasks = majorReleases.Select(entry =>
                (Entry: entry, ProgressTask: AddInactiveTask(ctx, $"Major: {entry.Version}"))).ToList();

            var copilotWork = RunTrackedTaskAsync(copilotTask, copilotLabel,
                () => newsletterService.GenerateDevTechCopilotSectionAsync(
                    cliReleases, sdkReleases, changelogEntries,
                    weekStart, weekEnd, cache, selectedModel),
                metrics, "Generate: Copilot section");

            var vscodeWork = RunTrackedTaskAsync(vscodeTask, vscodeLabel,
                () => newsletterService.GenerateDevTechVSCodeSectionAsync(
                    vscodeReleaseNotes, vscodeBlogEntries,
                    weekStart, weekEnd, cache, selectedModel),
                metrics, "Generate: VS Code section");

            var vsWork = RunTrackedTaskAsync(vsTask, vsLabel,
                () => newsletterService.GenerateDevTechVisualStudioSectionAsync(
                    vsBlogEntries, FeedUrls.VSReleaseNotes, FeedUrls.VSInsidersReleaseNotes,
                    weekStart, weekEnd, cache, selectedModel),
                metrics, "Generate: Visual Studio section");

            var majorReleaseWork = majorTasks.Select(mrt =>
                RunTrackedTaskAsync(mrt.ProgressTask, $"Major: {mrt.Entry.Version}",
                    () => newsletterService.GenerateDevTechMajorReleaseSectionAsync(
                        mrt.Entry, weekStart, weekEnd, cache, selectedModel),
                    metrics, $"Generate: Major release ({mrt.Entry.Version})")).ToList();

            var blogsWork = RunTrackedTaskAsync(blogsTask, blogsLabel,
                () => newsletterService.GenerateDevTechBlogsSectionAsync(
                    blogPool, majorReleaseTitles, weekStart, weekEnd, cache, selectedModel),
                metrics, "Generate: Developer Blogs section");

            var videosWork = RunTrackedTaskAsync(videosTask, videosLabel,
                () => newsletterService.GenerateDevTechVideosSectionAsync(
                    youtubeDotNetEntries, youtubeVSEntries, youtubeVSCodeEntries,
                    youtubeGitHubEntries, youtubeMicrosoftDevEntries,
                    weekStart, weekEnd, cache, selectedModel),
                metrics, "Generate: Developer Videos section");

            await Task.WhenAll(
                copilotWork, vscodeWork, vsWork,
                Task.WhenAll(majorReleaseWork),
                blogsWork, videosWork);

            copilotSection = await copilotWork;
            vscodeSection = await vscodeWork;
            vsSection = await vsWork;
            foreach (var work in majorReleaseWork)
                majorReleaseSections.Add(await work);
            blogsSection = await blogsWork;
            videosSection = await videosWork;
        });

        // Phase 2: Generate Welcome (needs all sections) and Title
        List<string> allSections = [copilotSection, vscodeSection, vsSection];
        allSections.AddRange(majorReleaseSections);
        allSections.AddRange([blogsSection, videosSection]);

        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            const string welcomeLabel = "Welcome section";
            const string titleLabel = "Newsletter title";

            var welcomeTask = AddInactiveTask(ctx, welcomeLabel);
            var titleTask = AddInactiveTask(ctx, titleLabel);

            welcomeSection = await RunTrackedTaskAsync(welcomeTask, welcomeLabel,
                () => newsletterService.GenerateDevTechWelcomeAsync(
                    allSections, weekStart, weekEnd, cache, selectedModel),
                metrics, "Generate: DevTech Welcome");

            var welcomeSummary = ExtractWelcomeSummary(welcomeSection);
            var newsletterLabel = GetNewsletterLabel(NewsletterType.DevTechMVP);
            title = await RunTrackedTaskAsync(titleTask, titleLabel,
                () => newsletterService.GenerateNewsletterTitleAsync(
                    welcomeSummary, newsletterLabel, cache, selectedModel),
                metrics, "Generate: Newsletter title");
        });

        // Assemble final content
        var contentBuilder = new StringBuilder();
        contentBuilder.AppendLine(welcomeSection);
        contentBuilder.AppendLine();

        List<string> allBodySections = [copilotSection, vscodeSection, vsSection, ..majorReleaseSections, blogsSection, videosSection];
        foreach (var section in allBodySections.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            contentBuilder.AppendLine(section);
            contentBuilder.AppendLine();
        }

        content = contentBuilder.ToString().Trim();

        if (string.IsNullOrWhiteSpace(content))
        {
            AnsiConsole.MarkupLine("[yellow]⚠[/] Empty DevTech newsletter result.");
            return (null, defaultTitle);
        }

        return (content, title);
    }

    private static (List<ReleaseEntry> Releases, PrereleaseConsolidationResult Consolidation) ConsolidateAndTrack(
        string sourceName,
        List<ReleaseEntry> releases,
        FeedFetchResult? fetchResult,
        RunMetrics metrics)
    {
        var consolidation = AtomFeedService.ConsolidatePrereleasesWithMetrics(releases);

        metrics.PrereleaseCounts.Add(new PrereleaseCount(
            sourceName,
            fetchResult?.MatchedItems ?? 0,
            consolidation.Releases.Select(r => r.Version).ToList(),
            consolidation.RolledUpPrereleases,
            consolidation.SkippedPrereleases));

        return (consolidation.Releases, consolidation);
    }

    private static string FormatCountCell(int n) => n == 0 ? "[dim]0[/]" : $"[green]{n}[/]";

    private static string FormatItemsCell(IEnumerable<ReleaseEntry> entries, int max = 3)
    {
        var titles = entries.Take(max)
            .Select(e => $"[white]{Markup.Escape(e.Version.Length > 40 ? e.Version[..40] + "..." : e.Version)}[/]");
        var list = string.Join(", ", titles);
        return list.Length == 0 ? "[dim]none[/]" : list;
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
