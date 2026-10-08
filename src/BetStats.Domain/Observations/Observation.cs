using BetStats.Domain.Common;
using BetStats.Domain.Identity;
using BetStats.Domain.Sports;

namespace BetStats.Domain.Observations;

public enum ObservationType { DisplayName, ScheduledStart, EventStatus, EventDate }

public sealed class Observation
{
    private Observation() { }
    public Observation(Guid id, ProviderIdentity identity, CanonicalReference? target, ObservationType type,
        DateTime retrievedAtUtc, DateTime availableAtUtc, DateTime createdAtUtc,
        string? textValue = null, DateTime? timestampValueUtc = null, SportingEventStatus? statusValue = null,
        DateTime? sourceEventTimeUtc = null, DateTime? sourcePublishedAtUtc = null, Guid? rawPayloadId = null,
        Observation? corrects = null, DateOnly? dateValue = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Id = Require.Id(id); ProviderIdentityId = identity.Id; DataSourceId = identity.DataSourceId; EntityKind = identity.EntityKind;
        Type = Require.Defined(type);
        Require.That(target is null || target.Kind == EntityKind, "Canonical target kind must match source identity.");
        RetrievedAtUtc = Require.Utc(retrievedAtUtc); AvailableAtUtc = Require.Utc(availableAtUtc); CreatedAtUtc = Require.Utc(createdAtUtc);
        Require.That(availableAtUtc >= retrievedAtUtc && createdAtUtc >= retrievedAtUtc, "Availability and creation cannot precede retrieval.");
        SourceEventTimeUtc = Require.Utc(sourceEventTimeUtc); SourcePublishedAtUtc = Require.Utc(sourcePublishedAtUtc);
        TimestampValueUtc = Require.Utc(timestampValueUtc);
        DateValue = dateValue;
        StatusValue = statusValue is { } status ? Require.Defined(status) : null;
        TextValue = textValue is null ? null : Require.Text(textValue, 200);
        Require.That(type switch
        {
            ObservationType.DisplayName => textValue is not null && timestampValueUtc is null && statusValue is null && dateValue is null && EntityKind != CanonicalEntityKind.SportingEvent,
            ObservationType.ScheduledStart => textValue is null && timestampValueUtc is not null && statusValue is null && dateValue is null && EntityKind == CanonicalEntityKind.SportingEvent,
            ObservationType.EventStatus => textValue is null && timestampValueUtc is null && statusValue is not null && dateValue is null && EntityKind == CanonicalEntityKind.SportingEvent,
            ObservationType.EventDate => textValue is null && timestampValueUtc is null && statusValue is null && dateValue is not null && EntityKind == CanonicalEntityKind.SportingEvent,
            _ => false
        }, "Observation value/type/entity kind is inconsistent.");
        Require.That(corrects is null || (corrects.ProviderIdentityId == identity.Id && corrects.Type == type &&
            corrects.Id != id && availableAtUtc >= corrects.AvailableAtUtc), "Correction must reference the same stream without backdating.");
        Version = corrects is null ? 1 : checked(corrects.Version + 1);
        CorrectsObservationId = corrects?.Id; CorrectedVersion = corrects?.Version; CorrectedAvailableAtUtc = corrects?.AvailableAtUtc;
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
    public ObservationType Type { get; private set; }
    public string? TextValue { get; private set; }
    public DateTime? TimestampValueUtc { get; private set; }
    public SportingEventStatus? StatusValue { get; private set; }
    public DateOnly? DateValue { get; private set; }
    public DateTime? SourceEventTimeUtc { get; private set; }
    public DateTime? SourcePublishedAtUtc { get; private set; }
    public DateTime RetrievedAtUtc { get; private set; }
    public DateTime AvailableAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }
    public Guid? RawPayloadId { get; private set; }
    public int Version { get; private set; }
    public Guid? CorrectsObservationId { get; private set; }
    public int? CorrectedVersion { get; private set; }
    public DateTime? CorrectedAvailableAtUtc { get; private set; }
    public Guid? CanonicalSportId { get; private set; }
    public Guid? CanonicalCompetitionId { get; private set; }
    public Guid? CanonicalSeasonId { get; private set; }
    public Guid? CanonicalParticipantId { get; private set; }
    public Guid? CanonicalSportingEventId { get; private set; }
    public Guid? CanonicalId => CanonicalSportId ?? CanonicalCompetitionId ?? CanonicalSeasonId ?? CanonicalParticipantId ?? CanonicalSportingEventId;
}
