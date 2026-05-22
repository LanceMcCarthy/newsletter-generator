using NewsletterGenerator.Models;
using NewsletterGenerator.Services;

namespace NewsletterGenerator;

internal sealed class RunMetrics
{
    public List<SourceCount> SourceCounts { get; } = [];
    public List<PrereleaseCount> PrereleaseCounts { get; } = [];
    public Dictionary<string, double> StageSeconds { get; } = [];
    public List<string> Warnings { get; } = [];
    public int CacheHits { get; set; }
    public int CacheMisses { get; set; }
    public int CacheSkips { get; set; }
    public IReadOnlyList<CacheSectionMetric> CacheSections { get; set; } = [];
    public double TotalWallSeconds { get; set; }
    public bool OverwroteOutput { get; set; }
    public string? OutputPath { get; set; }
    public int OutputCharacters { get; set; }
    public int OutputLines { get; set; }
    public int OutputSections { get; set; }
    public bool StreamingEnabled { get; set; } = true;
    public string ReasoningEffort { get; set; } = "low";
    public bool RevisionApplied { get; set; }
    public bool ClipboardSucceeded { get; set; }
    public NewsletterType Newsletter { get; set; } = NewsletterType.CopilotCliSdk;
    public string Model { get; set; } = string.Empty;
    public List<CopilotUsageMetric> CopilotUsage { get; } = [];
}

internal sealed record CopilotUsageMetric(
    string Operation,
    string SessionId,
    string? MessageId,
    int PromptCharacters,
    int OutputCharacters,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    DateTimeOffset CapturedAtUtc);

internal sealed record PreviewSection(string Heading, IReadOnlyList<string> Items);

internal sealed class AggregateRunMetrics
{
    public int TotalRuns { get; private set; }
    public Dictionary<NewsletterType, int> RunsByNewsletter { get; } = [];
    public int RevisedRuns { get; private set; }
    public int ClipboardSuccessRuns { get; private set; }
    public int CacheHits { get; private set; }
    public int CacheMisses { get; private set; }
    public int CacheSkips { get; private set; }
    public int OutputCharacters { get; private set; }
    public int OutputLines { get; private set; }
    public int WarningCount { get; private set; }
    public double TotalWallSeconds { get; private set; }
    public double AverageWallSeconds => TotalRuns == 0 ? 0 : TotalWallSeconds / TotalRuns;
    public int CopilotPromptCount { get; private set; }
    public long CopilotInputTokens { get; private set; }
    public long CopilotOutputTokens { get; private set; }
    public long CopilotTotalTokens { get; private set; }

    public void AddRun(RunMetrics metrics)
    {
        TotalRuns++;
        RunsByNewsletter[metrics.Newsletter] = RunsByNewsletter.GetValueOrDefault(metrics.Newsletter) + 1;
        if (metrics.RevisionApplied)
            RevisedRuns++;
        if (metrics.ClipboardSucceeded)
            ClipboardSuccessRuns++;

        CacheHits += metrics.CacheHits;
        CacheMisses += metrics.CacheMisses;
        CacheSkips += metrics.CacheSkips;
        OutputCharacters += metrics.OutputCharacters;
        OutputLines += metrics.OutputLines;
        WarningCount += metrics.Warnings.Count;
        TotalWallSeconds += metrics.TotalWallSeconds;

        CopilotPromptCount += metrics.CopilotUsage.Count;
        CopilotInputTokens += metrics.CopilotUsage.Sum(m => m.InputTokens ?? 0);
        CopilotOutputTokens += metrics.CopilotUsage.Sum(m => m.OutputTokens ?? 0);
        CopilotTotalTokens += metrics.CopilotUsage.Sum(m => m.TotalTokens ?? 0);
    }
}

internal sealed record SourceCount(string Source, string RawCount, string FilteredCount, string FinalCount, string Notes);
internal sealed record PrereleaseCount(
    string Source,
    int MatchedCount,
    IReadOnlyList<string> FinalReleaseVersions,
    IReadOnlyList<ConsolidatedPrerelease> RolledUpPrereleases,
    IReadOnlyList<string> SkippedPrereleases)
{
    public int FinalCount => FinalReleaseVersions.Count;
    public int DetectedCount => RolledUpPrereleases.Count + SkippedPrereleases.Count;
    public int RolledUpCount => RolledUpPrereleases.Count;
    public int SkippedCount => SkippedPrereleases.Count;
}
