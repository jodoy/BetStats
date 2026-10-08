using BetStats.Domain.Common;

namespace BetStats.Domain.Sports;

public sealed class Participant
{
    private Participant() { }
    public Participant(Guid id, Guid sportId, string name, ParticipantType participantType)
    {
        Id = Require.Id(id); SportId = Require.Id(sportId); Name = Require.Text(name, 200);
        ParticipantType = Require.Defined(participantType);
    }
    public Guid Id { get; private set; }
    public Guid SportId { get; private set; }
    public string Name { get; private set; } = null!;
    public ParticipantType ParticipantType { get; private set; }
}

public enum ParticipantType { Team, Individual }
