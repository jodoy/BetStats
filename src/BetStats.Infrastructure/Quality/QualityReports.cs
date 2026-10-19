using BetStats.Application.Quality;
using BetStats.Domain.Quality;
using Microsoft.EntityFrameworkCore;
using BetStats.Infrastructure.Persistence;

namespace BetStats.Infrastructure.Quality;

public sealed class QualityReports(BetStatsDbContext db, IAnalyticalQualityGate gate) : IQualityReports
{
    private sealed record ObservationMatch(Guid RawPayloadId, string RecordReference, Guid ObservationId);
    public async Task<QualityReport> ReadAsync(Guid executionId, int maximumRecords = 5000, CancellationToken token = default)
    {
        if (maximumRecords is < 1 or > 5000 || executionId == Guid.Empty) throw new ArgumentException("Execution and report bound 1..5000 required.");
        var rows = await db.QualityAssessments.AsNoTracking().Where(a => a.ExecutionId == executionId || a.RunId == executionId)
            .OrderBy(a => a.RawPayloadId).ThenBy(a => a.Row).ThenBy(a => a.RuleId).ThenBy(a => a.Id).Take(maximumRecords * 16 + 1).ToListAsync(token);
        if (rows.Count > maximumRecords * 16) throw new InvalidOperationException("Quality report exceeds bound.");
        // A run may have multiple executions: latest assessment per RAW/row/rule.
        var payloadFailures = rows.Where(a => a.Row == 0 && !a.Passed).Select(a => a.RawPayloadId).Distinct().Count();
        var groups = rows.Where(a => a.Row > 0).GroupBy(a => (a.RawPayloadId, a.Row)).ToArray();
        if (groups.Length > maximumRecords) throw new InvalidOperationException("Quality report exceeds record bound.");
        var invalid = 0; var duplicates = 0; var unresolved = 0; var conflicts = 0; var accepted = 0; var eligible = 0;
        var now = await QualityPersistence.Now(db, token);
        var acceptedRows = new List<(Guid RawPayloadId, string RecordReference)>();
        foreach (var row in groups)
        {
            var rules = row.GroupBy(a => a.RuleId).Select(g => g.OrderByDescending(a => a.RecordedAtUtc).ThenByDescending(a => a.Id).First()).ToArray();
            var issues = rules.Where(a => !a.Passed || a.BlocksEligibility).ToArray();
            // Exclusive outcome priority: invalid > duplicate > conflict > unresolved > accepted.
            if (issues.Any(a => a.Classification == QualityClassification.Invalid)) { invalid++; continue; }
            if (issues.Any(a => a.Classification == QualityClassification.DuplicateEquivalent)) { duplicates++; continue; }
            if (issues.Any(a => a.Classification is QualityClassification.DuplicateContradictory or QualityClassification.CanonicalMismatch or QualityClassification.ObservationConflict or QualityClassification.InvalidTransition)) { conflicts++; continue; }
            if (issues.Any(a => a.Classification == QualityClassification.IdentityAmbiguous)) { unresolved++; continue; }
            accepted++;
            acceptedRows.Add((row.Key.RawPayloadId, rules[0].RecordReference));
        }
        // LATERAL keeps the original ordered Take(2) independently for each
        // composite key. Even long observation histories materialize at most 200
        // matches per batch. A single match is required; never select First.
        foreach (var batch in acceptedRows.Chunk(100))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(batch.Distinct().Select(k => new { k.RawPayloadId, k.RecordReference }));
            var matches = await db.Database.SqlQuery<ObservationMatch>($"""
                SELECT k."RawPayloadId", k."RecordReference", o."Id" AS "ObservationId"
                FROM jsonb_to_recordset({json}::jsonb) AS k("RawPayloadId" uuid, "RecordReference" text)
                CROSS JOIN LATERAL (
                    SELECT o."Id" FROM provenance."Observations" o
                    JOIN provenance."ProviderIdentities" i ON i."Id" = o."ProviderIdentityId"
                    WHERE o."RawPayloadId" = k."RawPayloadId" AND i."ExternalId" = k."RecordReference"
                      AND o."Type" = {nameof(BetStats.Domain.Observations.ObservationType.EventDate)}
                    ORDER BY o."Id" LIMIT 2
                ) o
                """).ToListAsync(token);
            var byKey = matches.ToLookup(m => (m.RawPayloadId, m.RecordReference));
            var queries = batch.Select(key => byKey[key].ToArray()).Where(observations => observations.Length == 1)
                .Select(observations => new EligibilityQuery(observations[0].ObservationId, now, BetStats.Domain.Governance.DataPurpose.InternalAnalytics, new())).ToArray();
            if (queries.Length > 0) eligible += (await gate.EvaluateManyAsync(queries, token)).Count(result => result.Eligible);
        }
        var total = groups.Length; var valid = total - invalid - duplicates - conflicts; var rejected = invalid + duplicates + conflicts;
        double Rate(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;
        return new(executionId, total, valid, invalid, duplicates, unresolved, conflicts, accepted, rejected, eligible,
            Rate(valid, total), Rate(accepted, valid), Rate(conflicts, total), Rate(eligible, total),
            rows.Select(a => a.RuleId + "@" + a.RuleVersion).Distinct().Order().ToArray(), rows.Count == 0 ? null : rows.Max(a => a.AssessedAtUtc),
            rows.Where(a => a.PolicyId != null).Select(a => a.PolicyId!.Value).Distinct().Order().ToArray(), payloadFailures);
    }
}
