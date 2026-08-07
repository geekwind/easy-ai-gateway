using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EasyGateway.Data;
using EasyGateway.Data.Entities;
using EasyGateway.Gateway;
using EasyGateway.Models;
using EasyGateway.Providers;
using EasyGateway.Services;

namespace EasyGateway.Endpoints;

/// <summary>
/// Admin REST API consumed by the Blazor UI. CRUD over services/models/keys,
/// connection test, usage stats, seed. All under /admin/*.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin");

        // Services
        g.MapGet("/services", ListServices);
        g.MapPost("/services", CreateService);
        g.MapPut("/services/{id}", UpdateService);
        g.MapPut("/services/{id}/enable", SetServiceEnabled);
        g.MapDelete("/services/{id}", DeleteService);
        g.MapPost("/services/{id}/test", TestService);
        g.MapPost("/services/{id}/discover-models", DiscoverModels);
        g.MapPost("/services/{id}/clone", CloneService);

        // Models
        g.MapGet("/services/{id}/models", ListModels);
        g.MapPost("/services/{id}/models", CreateModel);
        g.MapPut("/models/{id}", UpdateModel);
        g.MapDelete("/models/{id}", DeleteModel);

        // API keys
        g.MapGet("/apikeys", ListApiKeys);
        g.MapPost("/apikeys", CreateApiKey);
        g.MapDelete("/apikeys/{id}", DeleteApiKey);

        // Usage
        g.MapGet("/usage", GetUsage);
        g.MapGet("/usage/hourly", GetUsageHourly);
        g.MapGet("/usage/filters", GetUsageFilters);

        // Call logs (detailed per-request log with cache hit + token consumption)
        g.MapGet("/call-logs", GetCallLogs);

        // Dispatch trace (for sticky-session / failover observability)
        g.MapGet("/dispatch-trace", GetDispatchTrace);

        // Live per-service adaptive-LB state (in-flight, EWMA, breaker) for observability
        g.MapGet("/service-state", GetServiceState);

        // Provider types (for UI dropdown)
        g.MapGet("/provider-types", GetProviderTypes);

        // App settings (software name, subtitle, ...)
        g.MapGet("/settings", GetSettings);
        g.MapPut("/settings", SaveSettings);

        // Restart the whole application (new process, re-reads listen_host/port).
        g.MapPost("/system/restart", RestartApplication);

        // Seed demo config
        g.MapPost("/seed", Seed);

        return app;
    }

    // --- Services ---

    private static async Task<IResult> ListServices(
        [FromServices] IDbContextFactory<AppDbContext> dbf) =>
        await QueryAsync(dbf, db => db.Services.Include(s => s.Models).AsNoTracking().ToListAsync());

    private static async Task<IResult> CreateService(
        [FromBody] ServiceEntity svc, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        db.Services.Add(svc);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(svc);
    }

    private static async Task<IResult> UpdateService(
        int id, [FromBody] ServiceEntity svc, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var existing = await db.Services.FindAsync(id);
        if (existing is null) return Results.NotFound();
        existing.ProviderType = svc.ProviderType;
        existing.Name = svc.Name;
        existing.Enabled = svc.Enabled;
        existing.ServerUrl = svc.ServerUrl;
        existing.Weight = svc.Weight;
        existing.Priority = svc.Priority;
        existing.CredentialsJson = svc.CredentialsJson;
        existing.LimitJson = svc.LimitJson;
        existing.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(existing);
    }

    /// <summary>Toggle a service's enabled flag without touching its other fields
    /// (handy for testing load balancing by isolating services).</summary>
    private static async Task<IResult> SetServiceEnabled(
        int id, [FromBody] System.Text.Json.JsonElement body,
        [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var existing = await db.Services.FindAsync(id);
        if (existing is null) return Results.NotFound();
        if (body.TryGetProperty("enabled", out var en))
            existing.Enabled = en.GetBoolean();
        existing.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(new { existing.Id, existing.Enabled });
    }

    private static async Task<IResult> DeleteService(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var svc = await db.Services.Include(s => s.Models).FirstOrDefaultAsync(s => s.Id == id);
        if (svc is null) return Results.NotFound();
        db.Services.Remove(svc);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Ok();
    }

    /// <summary>Duplicate a service together with all its models. The copy is
    /// created disabled so the user can adjust credentials before traffic hits it.</summary>
    private static async Task<IResult> CloneService(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var src = await db.Services.Include(s => s.Models).AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);
        if (src is null) return Results.NotFound();

        var copy = new ServiceEntity
        {
            ProviderType = src.ProviderType,
            Name = src.Name + " (副本)",
            Enabled = false,
            ServerUrl = src.ServerUrl,
            Weight = src.Weight,
            Priority = src.Priority,
            CredentialsJson = src.CredentialsJson,
            LimitJson = src.LimitJson,
            ModelRedirectJson = src.ModelRedirectJson,
            ModelMapJson = src.ModelMapJson,
        };
        foreach (var m in src.Models)
        {
            copy.Models.Add(new ModelEntity
            {
                ModelName = m.ModelName,
                UpstreamModel = m.UpstreamModel,
                Aliases = m.Aliases,
                Enabled = m.Enabled,
                SupportsVision = m.SupportsVision,
                SupportsTools = m.SupportsTools,
                SupportsReasoning = m.SupportsReasoning,
                SupportsEmbeddings = m.SupportsEmbeddings,
            });
        }
        db.Services.Add(copy);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(new { ok = true, id = copy.Id, models = copy.Models.Count }, AdminJsonOpts);
    }

    // --- Models ---

    private static async Task<IResult> ListModels(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf) =>
        await QueryAsync(dbf, db => db.Models.Where(m => m.ServiceId == id).AsNoTracking().ToListAsync());

    private static async Task<IResult> CreateModel(
        int id, [FromBody] ModelEntity model, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        model.ServiceId = id;
        db.Models.Add(model);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(model);
    }

    private static async Task<IResult> UpdateModel(
        int id, [FromBody] ModelEntity model, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var existing = await db.Models.FindAsync(id);
        if (existing is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(model.ModelName))
            return Results.BadRequest("ModelName is required");
        existing.ModelName = model.ModelName.Trim();
        existing.UpstreamModel = model.UpstreamModel?.Trim() ?? "";
        existing.Aliases = model.Aliases?.Trim() ?? "";
        existing.Enabled = model.Enabled;
        existing.SupportsVision = model.SupportsVision;
        existing.SupportsTools = model.SupportsTools;
        existing.SupportsReasoning = model.SupportsReasoning;
        existing.SupportsEmbeddings = model.SupportsEmbeddings;
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(existing);
    }

    private static async Task<IResult> DeleteModel(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var m = await db.Models.FindAsync(id);
        if (m is null) return Results.NotFound();
        db.Models.Remove(m);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Ok();
    }

    // --- API Keys ---

    private static async Task<IResult> ListApiKeys(
        [FromServices] IDbContextFactory<AppDbContext> dbf) =>
        await QueryAsync(dbf, db => db.ApiKeys.AsNoTracking().ToListAsync());

    private static async Task<IResult> CreateApiKey(
        [FromBody] ApiKeyEntity key, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        if (string.IsNullOrEmpty(key.KeyValue))
            key.KeyValue = "sk-" + Guid.NewGuid().ToString("N");
        await using var db = await dbf.CreateDbContextAsync();
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(key);
    }

    private static async Task<IResult> DeleteApiKey(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var k = await db.ApiKeys.FindAsync(id);
        if (k is null) return Results.NotFound();
        db.ApiKeys.Remove(k);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Ok();
    }

    // --- Usage / test / seed ---

    private static async Task<IResult> GetUsage(
        [FromQuery] int? hours, [FromQuery] string? model, [FromQuery] string? service,
        [FromQuery] string? provider, [FromServices] UsageService usage, CancellationToken ct) =>
        Results.Json(await usage.GetStatsAsync(NormHours(hours), model, service, provider, ct));

    private static async Task<IResult> GetUsageHourly(
        [FromQuery] int? hours, [FromQuery] string? model, [FromQuery] string? service,
        [FromQuery] string? provider, [FromServices] UsageService usage, CancellationToken ct) =>
        Results.Json(await usage.GetHourlyAsync(NormHours(hours), model, service, provider, ct));

    private static async Task<IResult> GetUsageFilters(
        [FromServices] UsageService usage, CancellationToken ct) =>
        Results.Json(await usage.GetFilterOptionsAsync(ct));

    private static int NormHours(int? h) => h is > 0 and <= 24 * 90 ? h.Value : 24;

    private static IResult GetDispatchTrace() =>
        Results.Json(GatewayService.DispatchTraces, AdminJsonOpts);

    private static IResult GetServiceState([FromServices] GatewayService gw) =>
        Results.Json(gw.ServiceStates(), AdminJsonOpts);

    private static async Task<IResult> GetCallLogs(
        [FromQuery] int limit,
        [FromQuery] string? model,
        [FromQuery] string? service,
        [FromQuery] string? provider,
        [FromQuery] string? status,
        [FromQuery] string? q,
        [FromServices] IDbContextFactory<AppDbContext> dbf,
        CancellationToken ct)
    {
        var lim = limit <= 0 || limit > 500 ? 50 : limit;
        await using var db = await dbf.CreateDbContextAsync(ct);
        var qr = db.UsageLogs.AsNoTracking() as IQueryable<UsageLogEntity>;
        if (!string.IsNullOrWhiteSpace(model)) qr = qr.Where(l => l.Model == model);
        if (!string.IsNullOrWhiteSpace(service)) qr = qr.Where(l => l.ServiceName == service);
        if (!string.IsNullOrWhiteSpace(provider)) qr = qr.Where(l => l.ProviderType == provider);
        if (status == "success") qr = qr.Where(l => l.Success);
        else if (status == "failed") qr = qr.Where(l => !l.Success);
        if (!string.IsNullOrWhiteSpace(q))
            qr = qr.Where(l => l.Model.Contains(q) || l.UpstreamModel.Contains(q)
                || l.ServiceName.Contains(q) || l.PromptPreview.Contains(q) || l.ResponsePreview.Contains(q));
        var logs = await qr.OrderByDescending(l => l.Timestamp).Take(lim).ToListAsync(ct);
        return Results.Json(logs.Select(l => new
        {
            timestamp = l.Timestamp,
            model = l.Model,
            upstreamModel = l.UpstreamModel,
            service = l.ServiceName,
            provider = l.ProviderType,
            apiKey = l.ApiKeyName,
            success = l.Success,
            statusCode = l.StatusCode,
            promptTokens = l.PromptTokens,
            completionTokens = l.CompletionTokens,
            totalTokens = l.TotalTokens,
            reasoningTokens = l.ReasoningTokens,
            cacheCreationTokens = l.CacheCreationTokens,
            cacheReadTokens = l.CacheReadTokens,
            cacheHit = l.CacheHit,
            latencyMs = l.LatencyMs,
            ttftMs = l.TtftMs,
            stream = l.Stream,
            sessionId = string.IsNullOrEmpty(l.SessionId) ? null : l.SessionId,
            promptPreview = l.PromptPreview,
            responsePreview = l.ResponsePreview,
            error = string.IsNullOrEmpty(l.Error) ? null : l.Error,
        }), AdminJsonOpts);
    }

    private static async Task<IResult> GetProviderTypes(
        [FromServices] IProviderRegistry registry) =>
        Results.Json(registry.RegisteredTypes);

    // --- Settings ---

    private static IResult GetSettings([FromServices] AppSettingsService settings) =>
        Results.Json(new Dictionary<string, string>
        {
            [AppSettingsService.KeyAppName] = settings.AppName,
            [AppSettingsService.KeySubtitle] = settings.Subtitle,
            [AppSettingsService.KeyLogoType] = settings.LogoType,
            [AppSettingsService.KeyLogoValue] = settings.LogoValue,
            // listen_host/port take effect only after a restart, but return the
            // stored value so the UI can show what's currently configured.
            [AppSettingsService.KeyListenHost] = settings.ListenHost,
            [AppSettingsService.KeyListenPort] = settings.ListenPort.ToString(),
        }, AdminJsonOpts);

    private static async Task<IResult> SaveSettings(
        [FromBody] Dictionary<string, string> incoming,
        [FromServices] AppSettingsService settings,
        CancellationToken ct)
    {
        foreach (var key in new[]
        {
            AppSettingsService.KeyAppName,
            AppSettingsService.KeySubtitle,
            AppSettingsService.KeyLogoType,
            AppSettingsService.KeyLogoValue,
            AppSettingsService.KeyListenHost,
            AppSettingsService.KeyListenPort,
        })
            if (incoming.TryGetValue(key, out var v))
                await settings.SetAsync(key, v?.Trim() ?? "", ct);
        return Results.Json(new { ok = true }, AdminJsonOpts);
    }

    /// <summary>
    /// Restarts the whole application: spawns a fresh process carrying
    /// <c>--restart-of &lt;pid&gt;</c> (which waits for THIS process to exit so the
    /// port/mutex are released), then stops and exits the current one. Desktop
    /// mode relaunches the GUI with the new settings; a container without a
    /// restart policy just stops (rely on docker/systemd/k8s to restart it).
    /// </summary>
    private static IResult RestartApplication([FromServices] IHostApplicationLifetime life)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return Results.Json(new
            {
                ok = false,
                error = "无法确定可执行文件路径；容器/托管环境请依赖 restart 策略（docker restart=always / k8s / systemd）重启。"
            }, AdminJsonOpts);
        }

        try
        {
            // Environment.ProcessPath is our app exe for a published/self-contained
            // launch, but the `dotnet` host when launched via `dotnet run` or
            // `dotnet EasyGateway.dll`. Detect the host and, in the latter case,
            // relaunch as `dotnet <dll> --restart-of <pid>` so dev launches
            // restart correctly too. (Single-file publish has an empty assembly
            // Location, but isDotnetHost is false there so we never need it.)
            var hostName = Path.GetFileName(exe);
            var isDotnetHost = hostName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                || hostName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            if (isDotnetHost)
            {
                // Entry assembly = our app dll. Empty under single-file publish,
                // but isDotnetHost is false there so this branch never runs.
                var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrEmpty(dll))
                    return Results.Json(new { ok = false, error = "无法确定应用程序集路径以重启。" }, AdminJsonOpts);
                psi.ArgumentList.Add(dll);
            }
            psi.ArgumentList.Add("--restart-of");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = "拉起新进程失败：" + ex.Message }, AdminJsonOpts);
        }

        // Fire-and-forget: let the HTTP response flush, then stop the host and
        // force-exit. StopApplication alone won't end the WinForms message pump
        // in desktop mode, so Environment.Exit guarantees the old process dies
        // (releasing the port/mutex the new process is waiting on).
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            try { life.StopApplication(); } catch { /* best effort */ }
            await Task.Delay(300);
            Environment.Exit(0);
        });

        return Results.Json(new { ok = true, message = "正在重启，请稍候…" }, AdminJsonOpts);
    }

    private static async Task<IResult> DiscoverModels(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] IProviderRegistry registry,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var svc = await db.Services.Include(s => s.Models).FirstOrDefaultAsync(s => s.Id == id);
        if (svc is null) return Results.NotFound();

        // Build provider and check if it supports model listing.
        var provider = registry.Create(svc);
        if (provider is not IModelListable listable)
            return Results.Json(new { ok = false, reason = "not_supported", message = "该 Provider 类型不支持模型列举" }, AdminJsonOpts);

        List<UpstreamModelInfo> models;
        try { models = await listable.ListModelsAsync(); }
        catch (Exception ex) { return Results.Json(new { ok = false, reason = "upstream_error", message = ex.Message }, AdminJsonOpts); }

        // Dedup against existing models on this service (case-insensitive by ModelName).
        var existing = svc.Models.Select(m => m.ModelName.ToLowerInvariant()).ToHashSet();
        var imported = new List<string>();
        var skipped = new List<string>();
        foreach (var m in models)
        {
            if (string.IsNullOrEmpty(m.Id)) continue;
            var name = m.Id;
            if (existing.Contains(name.ToLowerInvariant()))
            { skipped.Add(name); continue; }
            db.Models.Add(new ModelEntity
            {
                ServiceId = id,
                ModelName = name,
                UpstreamModel = name,
                Aliases = m.DisplayName ?? "",
                Enabled = true,
                SupportsTools = true,
                SupportsVision = false,
                SupportsReasoning = false,
                SupportsEmbeddings = false,
            });
            existing.Add(name.ToLowerInvariant());
            imported.Add(name);
        }
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(new { ok = true, imported, skipped, total = models.Count }, AdminJsonOpts);
    }

    private static async Task<IResult> TestService(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] IProviderRegistry registry)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var svc = await db.Services.Include(s => s.Models).FirstOrDefaultAsync(s => s.Id == id);
        if (svc is null) return Results.NotFound();
        try
        {
            var provider = registry.Create(svc);
            var mdl = svc.Models.FirstOrDefault();
            var modelName = mdl?.ResolveUpstreamModel() ?? "gpt-3.5-turbo";
            var req = new ChatRequest
            {
                Model = modelName,
                ClientModel = mdl?.ModelName ?? modelName,
                MaxTokens = 5,
                Messages = new() { new() { Role = "user", Content = "hi" } },
            };
            var resp = await provider.ChatAsync(req);
            return Results.Json(new { ok = true, model = resp.Model });
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = ex.Message });
        }
    }

    private static async Task<IResult> Seed(
        [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        if (await db.Services.AnyAsync())
            return Results.Ok(new { seeded = false, reason = "already has services" });

        // Create a placeholder service — user must fill in their own upstream URL
        // and API key via the admin UI before use. No real credentials here.
        var svc = new ServiceEntity
        {
            ProviderType = "openai",
            Name = "My Upstream (configure me)",
            Enabled = false,
            ServerUrl = "https://api.example.com/v1",
            CredentialsJson = """{"api_key":"your-api-key-here"}""",
        };
        db.Services.Add(svc);
        await db.SaveChangesAsync();

        // A few common model names as examples (user should adjust to match
        // their upstream's actual model list, or use "发现模型" to auto-discover).
        foreach (var m in new[]
        {
            ("gpt-4o", true, true, false, false),
            ("gpt-4o-mini", true, true, false, false),
            ("claude-3-5-sonnet-latest", true, true, false, false),
        })
        {
            db.Models.Add(new ModelEntity
            {
                ServiceId = svc.Id,
                ModelName = m.Item1,
                Enabled = true,
                SupportsVision = m.Item2,
                SupportsTools = m.Item3,
                SupportsReasoning = m.Item4,
                SupportsEmbeddings = m.Item5,
            });
        }

        db.ApiKeys.Add(new ApiKeyEntity
        {
            KeyValue = "sk-easygateway-local",
            Name = "local",
            Enabled = true,
            AllowedModels = "*",
        });

        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Ok(new { seeded = true, services = 1, models = 4, keys = 1 });
    }

    // --- helpers ---

    private static readonly JsonSerializerOptions AdminJsonOpts = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static async Task<IResult> QueryAsync<T>(
        IDbContextFactory<AppDbContext> dbf, Func<AppDbContext, Task<List<T>>> query) =>
        Results.Json(await query(await dbf.CreateDbContextAsync()), AdminJsonOpts);
}
