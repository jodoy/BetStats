using BetStats.Domain.Common;

namespace BetStats.Domain.Sports;

public sealed class EventParticipant
{
    private EventParticipant() { }
    internal EventParticipant(SportingEvent sportingEvent, Participant participant, ParticipantRole role, int position)
    {
        Role = Require.Defined(role);
        Require.That(position is 1 or 2, "Two-sided events use positions 1 and 2.");
        Require.That(position == (role is ParticipantRole.Home or ParticipantRole.Side1 ? 1 : 2), "Role does not match position.");
        EventId = sportingEvent.Id; ParticipantId = participant.Id; SportId = sportingEvent.SportId; Position = position;
    }
    public Guid EventId { get; private set; }
    public Guid ParticipantId { get; private set; }
    public Guid SportId { get; private set; }
    public ParticipantRole Role { get; private set; }
    public int Position { get; private set; }
}

public enum ParticipantRole { Home, Away, Side1, Side2 }
