using BetStats.Domain.Common;

namespace BetStats.Domain.Sports;

public sealed class SportingEvent
{
    private readonly List<EventParticipant> participants = [];
    private SportingEvent() { }
    public SportingEvent(Guid id, Competition competition, Season? season, DateTime? scheduledStartUtc,
        SportingEventStatus status, DateTime createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(competition);
        Require.That(season is null || season.CompetitionId == competition.Id, "Season belongs to another competition.");
        Id = Require.Id(id); SportId = competition.SportId; CompetitionId = competition.Id; SeasonId = season?.Id;
        ScheduledStartUtc = Require.Utc(scheduledStartUtc); Status = Require.Defined(status); CreatedAtUtc = Require.Utc(createdAtUtc);
    }
    public Guid Id { get; private set; }
    public Guid SportId { get; private set; }
    public Guid CompetitionId { get; private set; }
    public Guid? SeasonId { get; private set; }
    public DateTime? ScheduledStartUtc { get; private set; }
    public SportingEventStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public IReadOnlyCollection<EventParticipant> Participants => participants.AsReadOnly();

    public EventParticipant AddParticipant(Participant participant, ParticipantRole role, int position)
    {
        ArgumentNullException.ThrowIfNull(participant);
        Require.That(participant.SportId == SportId, "Participant and event belong to different sports.");
        Require.That(!participants.Any(item => item.ParticipantId == participant.Id || item.Position == position), "Duplicate participant or position.");
        var membership = new EventParticipant(this, participant, role, position);
        participants.Add(membership);
        return membership;
    }
}

public enum SportingEventStatus { Scheduled, InProgress, Completed, Postponed, Cancelled }
