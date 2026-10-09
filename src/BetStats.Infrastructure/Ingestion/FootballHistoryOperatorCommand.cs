using System.Text.Json;
using BetStats.Application.Football;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Governance;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.Infrastructure.Ingestion;

public static class FootballHistoryOperatorCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration config, bool development, CancellationToken token = default)
    {
        if (!development) throw new InvalidOperationException("Football history commands require Development.");
        string Text(string key) => config["FootballHistory:" + key] ?? throw new ArgumentException("Required FootballHistory:" + key);
        Guid Id(string key) => Guid.Parse(Text(key));
        var actor = Text("OperatorId"); var reason = Text("Reason"); QualityPersistence.Operator(actor, reason);
        var db = services.GetRequiredService<BetStatsDbContext>();
        var operations = services.GetRequiredService<FootballImportOperations>();
        object? result; var success = true;
        switch (Text("Action"))
        {
            case "plan": result = await operations.PlanAsync(Id("SourceId"), Text("PayloadPath"), new(Text("Competition"), Text("Season")), token); break;
            case "capture": case "recover":
                if (config["FootballHistory:Approve"] != "true") throw new ArgumentException("Explicit import approval required.");
                var plan = JsonSerializer.Deserialize<FootballImportPlan>(Text("PlanJson")) ?? throw new ArgumentException("Plan required.");
                var imported = await operations.RunAsync(Id("OperationId"), plan, Text("PayloadPath"), actor, reason, Text("Action") == "recover", token);
                result = imported; success = imported.Outcome is ImportOutcome.Succeeded or ImportOutcome.Reused; break;
            case "inspect":
                var operation = await operations.InspectAsync(Id("OperationId"), token);
                if (operation is not null) await Authorize(services, operation.SourceId, token);
                result = operation; break;
            case "reconcile":
                if (config["FootballHistory:Approve"] != "true") throw new ArgumentException("Explicit reconciliation approval required.");
                result = await services.GetRequiredService<IDataReconciliation>().RunAsync([new(Id("RawId"), new(Text("Competition"), Text("Season")))], actor, reason, token);
                success = ((ReconciliationResult)result).Result == "Completed"; break;
            case "recover-storage":
                if (config["FootballHistory:Approve"] != "true") throw new ArgumentException("Explicit storage recovery approval required.");
                result = await new IngestionRecovery(db, services.GetRequiredService<IRawPayloadStore>(), services.GetRequiredService<IFootballIngestionPersistence>()).ReconcileStagedAsync(token); break;
            case "readiness": result = await ReadinessAsync(services, Id("SourceId"), token); break;
            default: throw new ArgumentException("Unsupported football history action.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Version = 1, Action = Text("Action"), OperatorId = actor, Reason = reason, Result = result }, new JsonSerializerOptions { WriteIndented = true }));
        return success ? 0 : 1;
    }

    private static async Task Authorize(IServiceProvider services, Guid source, CancellationToken token)
    {
        if (await services.GetRequiredService<ISourceOperationalStatus>().ReadAsync(source, token) != SourceOperationalStatus.Enabled)
            throw new IngestionDeniedException("source_disabled_or_missing");
        var now = await QualityPersistence.Now(services.GetRequiredService<BetStatsDbContext>(), token);
        var policy = await services.GetRequiredService<ISourcePolicyEvaluator>().EvaluateAsync(source, DataPurpose.InternalAnalytics, now, new(), token);
        if (!policy.Allowed) throw new IngestionDeniedException("policy_" + policy.Reason);
    }

    public static async Task<FootballReadinessReport> ReadinessAsync(IServiceProvider services, Guid source, CancellationToken token = default)
    {
        await Authorize(services, source, token);
        var db = services.GetRequiredService<BetStatsDbContext>();
        var rows = db.FootballResults.AsNoTracking().Where(r => r.SourceId == source && !db.FootballResults.Any(n => n.CorrectsId == r.Id));
        // Converted score values are assessed in memory under a bounded report limit.
        var retained = await rows.OrderBy(r => r.Id).Take(10001).ToListAsync(token);
        var seasons = new List<FootballReadinessSeason>();
        foreach (var group in retained.Take(10000).GroupBy(r => new { r.CompetitionId, r.SeasonId }).OrderBy(g => g.Key.CompetitionId).ThenBy(g => g.Key.SeasonId))
        {
            var league = await db.Competitions.Where(c => c.Id == group.Key.CompetitionId).Select(c => c.Name).SingleAsync(token);
            var season = await db.Seasons.Where(s => s.Id == group.Key.SeasonId).Select(s => s.Name).SingleAsync(token);
            seasons.Add(new(group.Key.CompetitionId, group.Key.SeasonId, league, season, group.Count(),
                group.Count(r => r.Value.FullTime.Home is null || r.Value.FullTime.Away is null), group.Count(r => BetStats.Application.Football.FootballResultRules.LabelEligible(r.Value))));
        }
        var samples = new List<FootballReadinessSample>();
        await services.GetRequiredService<IFootballIngestionPersistence>().EnsureParsingAllowedAsync(source, token);
        foreach (var r in retained.Take(20))
        {
            var raw = await db.RawPayloads.AsNoTracking().SingleAsync(x => x.Id == r.RawId, token); var integrity = "UnavailableManifest";
            if (raw.ByteLength is { } length)
            {
                try { await services.GetRequiredService<IRawPayloadStore>().ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, length), token); integrity = "HashVerified"; }
                catch (Exception e) when (e is IOException or InvalidDataException) { integrity = "CorruptOrUnavailable"; }
            }
            samples.Add(new(r.EventId, r.EventDate, r.RawId, r.PublishedAtUtc, r.RetrievedAtUtc, r.RecordedAtUtc, r.Value,
                BetStats.Application.Football.FootballResultRules.LabelEligible(r.Value),
                r.RecordedAtUtc.Date > r.EventDate.ToDateTime(TimeOnly.MinValue).Date ? "RetrospectiveImportedHistory" : "PreEventEvidenceNotEstablished", integrity));
        }
        return new("football-readiness-v1", source, seasons, samples, retained.Count > 10000, 0, null,
            "UnknownWithoutReviewedInventory", "NotCertifiedByImport", "NotCertifiedByImport",
            ["reviewed_inventory_required", "pre_event_recording_and_event_time_evidence_required", "BS-011_evaluation_required"]);
    }
}
