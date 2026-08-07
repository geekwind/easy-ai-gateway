using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using EasyGateway.Data;
using EasyGateway.Data.Entities;

namespace EasyGateway.Services;

/// <summary>
/// Writes usage logs asynchronously (fire-and-forget into a channel, drained
/// by a background worker) so the response path isn't blocked.
/// </summary>
public class UsageService
{
    private readonly IDbContextFactory<AppDbContext> _dbf;
    private readonly ILogger<UsageService> _log;
    private readonly Channel<UsageLogEntity> _ch;

    public UsageService(IDbContextFactory<AppDbContext> dbf, ILogger<UsageService> log)
    {
        _dbf = dbf; _log = log;
        _ch = Channel.CreateUnbounded<UsageLogEntity>();
        _ = Task.Run(DrainAsync);
    }

    public void Record(UsageLogEntity entry) => _ch.Writer.TryWrite(entry);

    private async Task DrainAsync()
    {
        await foreach (var e in _ch.Reader.ReadAllAsync())
        {
            try
            {
                await using var db = await _dbf.CreateDbContextAsync();
                db.UsageLogs.Add(e);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "failed to persist usage log");
            }
        }
    }

    /// <summary>Recent usage stats for the dashboard, optionally filtered.</summary>
    public async Task<UsageStats> GetStatsAsync(int hours = 24, string? model = null,
        string? service = null, string? provider = null, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var since = DateTime.Now.AddHours(-hours);
        var logs = await Filtered(db, since, model, service, provider).ToListAsync(ct);

        return new UsageStats(
            Total: logs.Count,
            Success: logs.Count(l => l.Success),
            Failed: logs.Count(l => !l.Success),
            PromptTokens: logs.Sum(l => l.PromptTokens),
            CompletionTokens: logs.Sum(l => l.CompletionTokens),
            TotalTokens: logs.Sum(l => l.TotalTokens),
            ReasoningTokens: logs.Sum(l => l.ReasoningTokens),
            CacheCreationTokens: logs.Sum(l => l.CacheCreationTokens),
            CacheReadTokens: logs.Sum(l => l.CacheReadTokens),
            CacheHitCount: logs.Count(l => l.CacheHit),
            AvgLatencyMs: logs.Count == 0 ? 0 : (long)Math.Round(logs.Average(l => (double)l.LatencyMs)),
            AvgTtftMs: logs.Where(l => l.TtftMs > 0).Select(l => (long?)l.TtftMs).Average() is { } ttft ? (long)Math.Round((double)ttft) : 0,
            ByModel: logs.GroupBy(l => l.Model)
                         .ToDictionary(g => g.Key, g => g.Count()),
            ByProvider: logs.GroupBy(l => l.ProviderType)
                           .ToDictionary(g => g.Key, g => g.Count()),
            ByService: logs.GroupBy(l => l.ServiceName)
                          .ToDictionary(g => g.Key, g => g.Count()));
    }
    /// <summary>Per-hour request/token buckets for the dashboard trend chart.
    /// Returns exactly <paramref name="hours"/> buckets ending at the current hour,
    /// including empty ones so the chart has a continuous time axis.</summary>
    public async Task<List<HourlyBucket>> GetHourlyAsync(int hours = 24, string? model = null,
        string? service = null, string? provider = null, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var now = DateTime.Now;
        var end = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        var start = end.AddHours(-(hours - 1));
        var logs = await Filtered(db, start, model, service, provider).ToListAsync(ct);

        var byHour = logs.GroupBy(l => new DateTime(l.Timestamp.Year, l.Timestamp.Month,
                l.Timestamp.Day, l.Timestamp.Hour, 0, 0))
            .ToDictionary(g => g.Key, g => g.ToList());

        var buckets = new List<HourlyBucket>(hours);
        for (var h = start; h <= end; h = h.AddHours(1))
        {
            byHour.TryGetValue(h, out var hl);
            buckets.Add(new HourlyBucket(
                Hour: h.ToString("HH:00"),
                Success: hl?.Count(l => l.Success) ?? 0,
                Failed: hl?.Count(l => !l.Success) ?? 0,
                TotalTokens: hl?.Sum(l => l.TotalTokens) ?? 0));
        }
        return buckets;
    }

    /// <summary>Distinct model/service/provider names present in the logs, for
    /// populating filter dropdowns.</summary>
    public async Task<FilterOptions> GetFilterOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var logs = db.UsageLogs.AsNoTracking();
        return new FilterOptions(
            await logs.Select(l => l.Model).Distinct().OrderBy(x => x).ToListAsync(ct),
            await logs.Select(l => l.ServiceName).Distinct().OrderBy(x => x).ToListAsync(ct),
            await logs.Select(l => l.ProviderType).Distinct().OrderBy(x => x).ToListAsync(ct));
    }

    private static IQueryable<UsageLogEntity> Filtered(AppDbContext db, DateTime since,
        string? model, string? service, string? provider)
    {
        var q = db.UsageLogs.AsNoTracking().Where(l => l.Timestamp >= since);
        if (!string.IsNullOrWhiteSpace(model)) q = q.Where(l => l.Model == model);
        if (!string.IsNullOrWhiteSpace(service)) q = q.Where(l => l.ServiceName == service);
        if (!string.IsNullOrWhiteSpace(provider)) q = q.Where(l => l.ProviderType == provider);
        return q;
    }
}

public record FilterOptions(List<string> Models, List<string> Services, List<string> Providers);

public record HourlyBucket(string Hour, int Success, int Failed, int TotalTokens);

public record UsageStats(
    int Total, int Success, int Failed,
    int PromptTokens, int CompletionTokens, int TotalTokens,
    int ReasoningTokens, int CacheCreationTokens, int CacheReadTokens, int CacheHitCount,
    long AvgLatencyMs, long AvgTtftMs,
    Dictionary<string, int> ByModel, Dictionary<string, int> ByProvider, Dictionary<string, int> ByService);
