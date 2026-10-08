using BetStats.Domain.Common;

namespace BetStats.Domain.Identity;

public enum ResolutionStatus { Unresolved, Ambiguous, Resolved }

public sealed class IdentityResolution
{
    private IdentityResolution() { }
    public IdentityResolution(Guid id, ProviderIdentity identity, ResolutionStatus status, CanonicalReference? target,
        string decidedBy, string reason, DateTime decidedAtUtc, IdentityResolution? previous = null, Guid? rawPayloadId = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Id = Require.Id(id); ProviderIdentityId = identity.Id; DataSourceId = identity.DataSourceId; EntityKind = identity.EntityKind;
        Status = Require.Defined(status); DecidedBy = Require.Text(decidedBy, 200); Reason = Require.Text(reason, 500);
        DecidedAtUtc = Require.Utc(decidedAtUtc);
        Require.That(decidedAtUtc >= identity.CreatedAtUtc, "Decision predates its identity.");
        Require.That(status == ResolutionStatus.Resolved ? target is not null && target.Kind == identity.EntityKind : target is null,
            "Only resolved identities have a target of the declared kind; ambiguity must not choose one.");
        Require.That(previous is null || (previous.ProviderIdentityId == identity.Id && previous.Id != id && decidedAtUtc >= previous.DecidedAtUtc),
            "Previous decision must belong to this identity and cannot be backdated.");
        Version = previous is null ? 1 : checked(previous.Version + 1);
        PreviousDecisionId = previous?.Id; PreviousVersion = previous?.Version; PreviousDecidedAtUtc = previous?.DecidedAtUtc;
        RawPayloadId = rawPayloadId is { } raw ? Require.Id(raw) : null;
        CanonicalSportId = target?.Kind == CanonicalEntityKind.Sport ? target.Id : null;
        CanonicalCompetitionId = target?.Kind == CanonicalEntityKind.Competition ? target.Id : null;
        CanonicalSeasonId = target?.Kind == CanonicalEntityKind.Season ? target.Id : null;
        CanonicalParticipantId = target?.Kind == CanonicalEntityKind.Participant ? target.Id : null;
        CanonicalSportingEventId = target?.Kind == CanonicalEntityKind.SportingEvent ? target.Id : null;
    }
    public Guid Id { get; private set; }
    public Guid ProviderIdentityId { get; private set; }
    public Guid DataSourceId { get; private set; }
    public CanonicalEntityKind EntityKind { get; private set; }
    public ResolutionStatus Status { get; private set; }
    public int Version { get; private set; }
    public Guid? PreviousDecisionId { get; private set; }
    public int? PreviousVersion { get; private set; }
    public DateTime? PreviousDecidedAtUtc { get; private set; }
    public DateTime DecidedAtUtc { get; private set; }
    // Assigned by PostgreSQL; caller decision time is not historical availability.
    public DateTime RecordedAtUtc { get; private set; }
    public string DecidedBy { get; private set; } = null!;
    public string Reason { get; private set; } = null!;
    public Guid? RawPayloadId { get; private set; }
    public Guid? CanonicalSportId { get; private set; }
    public Guid? CanonicalCompetitionId { get; private set; }
    public Guid? CanonicalSeasonId { get; private set; }
    public Guid? CanonicalParticipantId { get; private set; }
    public Guid? CanonicalSportingEventId { get; private set; }
    public Guid? CanonicalId => CanonicalSportId ?? CanonicalCompetitionId ?? CanonicalSeasonId ?? CanonicalParticipantId ?? CanonicalSportingEventId;
}
