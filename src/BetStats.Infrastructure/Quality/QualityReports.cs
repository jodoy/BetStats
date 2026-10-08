using BetStats.Application.Quality;
using BetStats.Domain.Quality;
using Microsoft.EntityFrameworkCore;
using BetStats.Infrastructure.Persistence;

namespace BetStats.Infrastructure.Quality;

public sealed class QualityReports(BetStatsDbContext db, IAnalyticalQualityGate gate) : IQualityReports
{
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
            var reference = rules[0].RecordReference;
            var observations = await db.Observations.AsNoTracking().Where(o => o.RawPayloadId == row.Key.RawPayloadId && o.Type == BetStats.Domain.Observations.ObservationType.EventDate &&
                db.ProviderIdentities.Any(i => i.Id == o.ProviderIdentityId && i.ExternalId == reference)).OrderBy(o => o.Id).Take(2).ToListAsync(token);
            if (observations.Count == 1 && (await gate.EvaluateAsync(new(observations[0].Id, now, BetStats.Domain.Governance.DataPurpose.InternalAnalytics, new()), token)).Eligible) eligible++;
        }
        var total = groups.Length; var valid = total - invalid - duplicates - conflicts; var rejected = invalid + duplicates + conflicts;
        double Rate(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;
        return new(executionId, total, valid, invalid, duplicates, unresolved, conflicts, accepted, rejected, eligible,
            Rate(valid, total), Rate(accepted, valid), Rate(conflicts, total), Rate(eligible, total),
            rows.Select(a => a.RuleId + "@" + a.RuleVersion).Distinct().Order().ToArray(), rows.Count == 0 ? null : rows.Max(a => a.AssessedAtUtc),
            rows.Where(a => a.PolicyId != null).Select(a => a.PolicyId!.Value).Distinct().Order().ToArray(), payloadFailures);
    }
}
