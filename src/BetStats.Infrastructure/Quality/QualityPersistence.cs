using BetStats.Application.Ingestion;
using BetStats.Application.Quality;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Quality;

internal static class QualityPersistence
{
    public static IEnumerable<ImportIssue> ExpandIssues(FootballParseResult parsed) => parsed.Issues.SelectMany(issue =>
        issue.Row == 0 && parsed.ParsedCount > 0 ? Enumerable.Range(2, parsed.ParsedCount).Select(row => new ImportIssue(row, issue.Code)) : [issue]);
    public static Task<DateTime> Now(BetStatsDbContext db, CancellationToken token) => db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
    public static Task Lock(BetStatsDbContext db, Guid source, CancellationToken token) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM ingestion.\"DataSources\" WHERE \"Id\" = {source} FOR UPDATE", token);
    public static void Operator(string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 || string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new ArgumentException("Explicit operator identifier and bounded reason are required.");
    }
    public static MaintenanceEvent Audit(Guid execution, int sequence, string action, string actor, string reason, Guid target, string result, DateTime now, Guid? previous = null, Guid? decision = null) =>
        new() { Id = Guid.NewGuid(), ExecutionId = execution, Sequence = sequence, Action = action, OperatorId = actor, Reason = reason,
            TargetId = target, Result = result, ExecutedAtUtc = now, PreviousReferenceId = previous, DecisionId = decision };
    public static void Record(BetStatsDbContext db, Guid execution, RawPayload raw, Guid? run, int row, string reference, Guid? identity,
        Guid? policy, DateTime now, IEnumerable<QualityIssue> issues, string? contextKey = null)
    {
        foreach (var issue in issues)
            db.QualityAssessments.Add(new() { Id = Guid.NewGuid(), ExecutionId = execution, DataSourceId = raw.DataSourceId, RawPayloadId = raw.Id,
                RunId = run, Row = row, RecordReference = reference, ContextKey = contextKey, ProviderIdentityId = identity, PolicyId = policy, SportId = FootballQualityRules.Football,
                RuleId = issue.Rule.Id, RuleVersion = issue.Rule.Version, Passed = issue.Passed, Severity = issue.Severity, BlocksEligibility = issue.BlocksEligibility,
                ReasonCode = issue.ReasonCode, Classification = issue.Classification, AssessedAtUtc = now });
    }
    public static Task<ProviderIdentity?> Anchor(BetStatsDbContext db, Guid source, CanonicalEntityKind kind, string reference, CancellationToken token) =>
        db.ProviderIdentities.AsNoTracking().SingleOrDefaultAsync(i => i.DataSourceId == source && i.EntityKind == kind && i.ExternalId == reference, token);
    public static Task<IdentityResolution?> Decision(BetStatsDbContext db, Guid? identity, DateTime now, CancellationToken token) =>
        db.IdentityResolutions.AsNoTracking().Where(d => d.ProviderIdentityId == identity && d.RecordedAtUtc <= now && d.DecidedAtUtc <= now).OrderByDescending(d => d.Version).FirstOrDefaultAsync(token);
    public static async Task<bool> ReceiptExists(BetStatsDbContext db, RawPayload raw, FootballImportScope scope, FootballMatchRecord row, DateTime now, CancellationToken token)
    {
        var batch = FootballPublicationKeys.Batch(scope, raw.ContentHashSha256, row.Result is null ? FootballDataCsvParser.Version : FootballResultsCsvParser.Version);
        if (await db.IngestionPublications.AnyAsync(p => p.DataSourceId == raw.DataSourceId && p.Key == batch && p.IsBatch, token)) return true;
        var targets = new List<Guid>();
        foreach (var reference in new[] { (CanonicalEntityKind.Competition, "provider:competition:" + row.CompetitionReference),
            (CanonicalEntityKind.Season, FootballDataCsvParser.SeasonReference(row.CompetitionReference, row.SeasonReference)),
            (CanonicalEntityKind.Participant, row.HomeReference), (CanonicalEntityKind.Participant, row.AwayReference), (CanonicalEntityKind.SportingEvent, row.MatchReference) })
        {
            var anchor = await Anchor(db, raw.DataSourceId, reference.Item1, reference.Item2, token);
            var decision = await Decision(db, anchor?.Id, now, token);
            if (decision?.CanonicalId is not { } target) return false;
            targets.Add(target);
        }
        var key = FootballPublicationKeys.Row(batch, row.MatchReference, targets[0], targets[1], targets[2], targets[3], targets[4]);
        return await db.IngestionPublications.AnyAsync(p => p.DataSourceId == raw.DataSourceId && p.Key == key, token);
    }
    public static async Task<(Guid? Identity, IReadOnlyList<QualityIssue> Issues)> Assess(BetStatsDbContext db, RawPayload raw, FootballMatchRecord record, DateTime now, CancellationToken token)
    {
        var comp = await Anchor(db, raw.DataSourceId, CanonicalEntityKind.Competition, "provider:competition:" + record.CompetitionReference, token);
        var season = await Anchor(db, raw.DataSourceId, CanonicalEntityKind.Season, FootballDataCsvParser.SeasonReference(record.CompetitionReference, record.SeasonReference), token);
        var home = await Anchor(db, raw.DataSourceId, CanonicalEntityKind.Participant, record.HomeReference, token);
        var away = await Anchor(db, raw.DataSourceId, CanonicalEntityKind.Participant, record.AwayReference, token);
        var match = await Anchor(db, raw.DataSourceId, CanonicalEntityKind.SportingEvent, record.MatchReference, token);
        var cd = await Decision(db, comp?.Id, now, token); var sd = await Decision(db, season?.Id, now, token);
        var hd = await Decision(db, home?.Id, now, token); var ad = await Decision(db, away?.Id, now, token); var ed = await Decision(db, match?.Id, now, token);
        var c = cd?.CanonicalCompetitionId is { } ci ? await db.Competitions.AsNoTracking().SingleAsync(c => c.Id == ci, token) : null;
        var s = sd?.CanonicalSeasonId is { } si ? await db.Seasons.AsNoTracking().SingleAsync(s => s.Id == si, token) : null;
        var h = hd?.CanonicalParticipantId is { } hi ? await db.Participants.AsNoTracking().SingleAsync(p => p.Id == hi, token) : null;
        var a = ad?.CanonicalParticipantId is { } ai ? await db.Participants.AsNoTracking().SingleAsync(p => p.Id == ai, token) : null;
        var e = ed?.CanonicalSportingEventId is { } ei ? await db.SportingEvents.AsNoTracking().Include(e => e.Participants).SingleAsync(e => e.Id == ei, token) : null;
        var matchId = match?.Id;
        var date = await db.Observations.AsNoTracking().Where(o => o.ProviderIdentityId == matchId && o.Type == ObservationType.EventDate).OrderByDescending(o => o.Version).FirstOrDefaultAsync(token);
        var status = await db.Observations.AsNoTracking().Where(o => o.ProviderIdentityId == matchId && o.Type == ObservationType.EventStatus).OrderByDescending(o => o.Version).FirstOrDefaultAsync(token);
        async Task<bool> Chain(Observation? o) => o is null || (o.Version == 1 ? o.CorrectsObservationId is null :
            await db.Observations.AnyAsync(p => p.Id == o.CorrectsObservationId && p.ProviderIdentityId == o.ProviderIdentityId && p.DataSourceId == o.DataSourceId && p.Type == o.Type && p.Version == o.Version - 1 && p.AvailableAtUtc <= o.AvailableAtUtc, token));
        var changed = date is not null && date.DateValue != record.MatchDate;
        var oldTarget = date?.CanonicalId;
        var stale = date is not null && (date.RetrievedAtUtc > raw.RetrievedAtUtc || (oldTarget is not null && ed?.CanonicalId is { } target && oldTarget != target));
        var missing = c is null || s is null || h is null || a is null || ed?.Status == ResolutionStatus.Ambiguous || (e is null && record.Status != SportingEventStatus.Completed && record.Result is null);
        var mismatch = e is not null && (e.SportId != FootballQualityRules.Football || e.CompetitionId != c?.Id || e.SeasonId != s?.Id);
        var assignments = e is not null && (!e.Participants.Any(p => p.ParticipantId == h?.Id && p.Role == ParticipantRole.Home) || !e.Participants.Any(p => p.ParticipantId == a?.Id && p.Role == ParticipantRole.Away));
        var facts = new FootballQualityEvidence(FootballQualityRules.Football, c?.SportId, s?.CompetitionId, c?.Id, record.MatchDate,
            s?.StartDate, s?.EndDate, h?.Id, a?.Id, h is null ? null : h.ParticipantType == ParticipantType.Team ? h.SportId : Guid.Empty,
            a is null ? null : a.ParticipantType == ParticipantType.Team ? a.SportId : Guid.Empty, missing, mismatch, assignments, date?.DateValue,
            status?.StatusValue, record.Status, date is null || raw.RetrievedAtUtc > date.RetrievedAtUtc, await Chain(date) && await Chain(status),
            raw.RecordedAtUtc != default && raw.CreatedAtUtc >= raw.RetrievedAtUtc && (match is null || match.DataSourceId == raw.DataSourceId), stale,
            changed && date!.RetrievedAtUtc == raw.RetrievedAtUtc && date.RawPayloadId != raw.Id,
            e is not null && await db.Observations.AnyAsync(o => o.DataSourceId != raw.DataSourceId && o.CanonicalSportingEventId == e.Id &&
                o.Type == ObservationType.EventDate && o.DateValue != record.MatchDate && o.AvailableAtUtc <= now && o.RecordedAtUtc <= now &&
                !db.Observations.Any(n => n.ProviderIdentityId == o.ProviderIdentityId && n.Type == o.Type && n.Version > o.Version && n.AvailableAtUtc <= now && n.RecordedAtUtc <= now), token));
        return (match?.Id, FootballQualityRules.Assess(facts));
    }
}
