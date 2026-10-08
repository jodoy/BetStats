using System.Data;
using BetStats.Application.Ingestion;
using BetStats.Application.Quality;
using BetStats.Domain.Identity;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Quality;

public sealed class IdentityReview(BetStatsDbContext db) : IIdentityReview
{
    public async Task<IReadOnlyList<IdentityReviewItem>> ListUnresolvedAsync(Guid sourceId, int limit = 100, CancellationToken token = default)
    {
        if (limit is < 1 or > 200 || sourceId == Guid.Empty) throw new ArgumentException("Source and limit 1..200 required.");
        var identities = await db.ProviderIdentities.AsNoTracking().Where(i => i.DataSourceId == sourceId &&
            !db.IdentityResolutions.Any(d => d.ProviderIdentityId == i.Id && d.Status == ResolutionStatus.Resolved &&
                !db.IdentityResolutions.Any(n => n.ProviderIdentityId == i.Id && n.Version > d.Version)))
            .OrderBy(i => i.Id).Take(limit).ToListAsync(token);
        var items = new List<IdentityReviewItem>();
        foreach (var identity in identities) items.Add(await InspectAsync(identity.Id, token));
        return items;
    }
    public async Task<IdentityReviewItem> InspectAsync(Guid identityId, CancellationToken token = default)
    {
        var identity = await db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.Id == identityId, token);
        var decision = await db.IdentityResolutions.AsNoTracking().Where(d => d.ProviderIdentityId == identityId).OrderByDescending(d => d.Version).FirstOrDefaultAsync(token);
        var raws = await db.Observations.AsNoTracking().Where(o => o.ProviderIdentityId == identityId && o.RawPayloadId != null).Select(o => o.RawPayloadId!.Value)
            .Union(db.IdentityResolutions.Where(d => d.ProviderIdentityId == identityId && d.RawPayloadId != null).Select(d => d.RawPayloadId!.Value))
            .Distinct().OrderBy(id => id).Take(101).ToListAsync(token);
        if (raws.Count > 100) throw new InvalidOperationException("Identity provenance exceeds bounded inspection; narrow the investigation.");
        return new(identity, decision, raws);
    }
    private async Task<Guid?> Sport(ProviderIdentity identity, CancellationToken token)
    {
        var decision = await db.IdentityResolutions.AsNoTracking().Where(d => d.ProviderIdentityId == identity.Id && d.Status == ResolutionStatus.Resolved).OrderByDescending(d => d.Version).FirstOrDefaultAsync(token);
        if (decision?.CanonicalId is { } target) return await TargetSport(identity.EntityKind, target, token);
        var sports = await db.QualityAssessments.AsNoTracking().Where(a => a.ProviderIdentityId == identity.Id && a.RuleId == "football.sport" && a.Passed)
            .Select(a => a.SportId).Distinct().OrderBy(id => id).Take(2).ToListAsync(token);
        if (sports.Count == 1) return sports[0];
        // Legacy unresolved anchors: require RAW-associated event/name observations
        // and reviewed football competition context in that same capture.
        var rawIds = db.Observations.Where(o => o.ProviderIdentityId == identity.Id && o.RawPayloadId != null).Select(o => o.RawPayloadId);
        var football = await db.Observations.AnyAsync(o => o.DataSourceId == identity.DataSourceId && rawIds.Contains(o.RawPayloadId) &&
            o.CanonicalSportingEventId != null && db.SportingEvents.Any(e => e.Id == o.CanonicalSportingEventId && e.SportId == FootballQualityRules.Football), token);
        return football ? FootballQualityRules.Football : null;
    }
    private async Task<Guid?> TargetSport(CanonicalEntityKind kind, Guid id, CancellationToken token) => kind switch
    {
        CanonicalEntityKind.Sport => await db.Sports.Where(s => s.Id == id).Select(s => (Guid?)s.Id).SingleOrDefaultAsync(token),
        CanonicalEntityKind.Competition => await db.Competitions.Where(c => c.Id == id).Select(c => (Guid?)c.SportId).SingleOrDefaultAsync(token),
        CanonicalEntityKind.Season => await (from s in db.Seasons join c in db.Competitions on s.CompetitionId equals c.Id where s.Id == id select (Guid?)c.SportId).SingleOrDefaultAsync(token),
        CanonicalEntityKind.Participant => await db.Participants.Where(p => p.Id == id).Select(p => (Guid?)p.SportId).SingleOrDefaultAsync(token),
        CanonicalEntityKind.SportingEvent => await db.SportingEvents.Where(e => e.Id == id).Select(e => (Guid?)e.SportId).SingleOrDefaultAsync(token),
        _ => null
    };
    public async Task<IReadOnlyList<CanonicalCandidate>> CandidatesAsync(Guid identityId, int limit = 50, CancellationToken token = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentException("Candidate limit 1..100 required.");
        var identity = await db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.Id == identityId, token);
        var sport = await Sport(identity, token);
        if (sport is null) return [];
        // Stable, read-only candidates. Labels are not matching authority.
        return identity.EntityKind switch
        {
            CanonicalEntityKind.Participant => await db.Participants.Where(p => p.SportId == sport).OrderBy(p => p.Id).Take(limit).Select(p => new CanonicalCandidate(identity.EntityKind, p.Id, p.Name)).ToListAsync(token),
            CanonicalEntityKind.Competition => await db.Competitions.Where(c => c.SportId == sport).OrderBy(c => c.Id).Take(limit).Select(c => new CanonicalCandidate(identity.EntityKind, c.Id, c.Name)).ToListAsync(token),
            CanonicalEntityKind.Season => await (from s in db.Seasons join c in db.Competitions on s.CompetitionId equals c.Id where c.SportId == sport orderby s.Id select new CanonicalCandidate(identity.EntityKind, s.Id, s.Name)).Take(limit).ToListAsync(token),
            CanonicalEntityKind.SportingEvent => await db.SportingEvents.Where(e => e.SportId == sport).OrderBy(e => e.Id).Take(limit).Select(e => new CanonicalCandidate(identity.EntityKind, e.Id, e.Id.ToString())).ToListAsync(token),
            _ => []
        };
    }
    public async Task<ReviewResult> DecideAsync(IdentityReviewCommand command, CancellationToken token = default)
    {
        QualityPersistence.Operator(command.OperatorId, command.Reason);
        if (!Enum.IsDefined(command.Action) || command.ExpectedVersion < 0) throw new ArgumentException("Valid action/version required.");
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await QualityPersistence.Lock(db, command.DataSourceId, token);
        var now = await QualityPersistence.Now(db, token);
        var identity = await db.ProviderIdentities.AsNoTracking().SingleOrDefaultAsync(i => i.Id == command.ProviderIdentityId, token);
        var previous = identity is null ? null : await db.IdentityResolutions.AsNoTracking().Where(d => d.ProviderIdentityId == identity.Id).OrderByDescending(d => d.Version).FirstOrDefaultAsync(token);
        var result = identity is null || identity.DataSourceId != command.DataSourceId ? "source_or_identity_mismatch" :
            (previous?.Version ?? 0) != command.ExpectedVersion ? "concurrent_review_conflict" : "accepted";
        if (result == "accepted" && command.Action == ReviewAction.Approve)
        {
            var targetSport = command.Target is { } target ? await TargetSport(target.Kind, target.Id, token) : null;
            var expectedSport = await Sport(identity!, token);
            if (command.Target is null || command.Target.Kind != identity!.EntityKind || targetSport is null) result = "invalid_canonical_target";
            else if (expectedSport is null || targetSport != expectedSport) result = "sport_context_mismatch";
        }
        else if (result == "accepted" && command.Target is not null) result = "unexpected_target";
        IdentityResolution? decision = null;
        if (result == "accepted")
        {
            decision = new(Guid.NewGuid(), identity!, command.Action == ReviewAction.Approve ? ResolutionStatus.Resolved :
                command.Action == ReviewAction.Ambiguous ? ResolutionStatus.Ambiguous : ResolutionStatus.Unresolved, command.Target,
                command.OperatorId, command.Reason, now, previous, previous?.RawPayloadId);
            db.IdentityResolutions.Add(decision);
        }
        var audit = QualityPersistence.Audit(Guid.NewGuid(), 1, "Identity" + command.Action, command.OperatorId, command.Reason, command.ProviderIdentityId, result, now, previous?.Id, decision?.Id);
        db.MaintenanceEvents.Add(audit); await db.SaveChangesAsync(token); await tx.CommitAsync(token);
        return new(result, decision?.Id, decision?.Version ?? previous?.Version ?? 0, audit.Id);
    }
}
