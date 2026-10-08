using BetStats.Domain.Common;

namespace BetStats.Domain.Identity;

public enum CanonicalEntityKind { Sport, Competition, Season, Participant, SportingEvent }

public sealed record CanonicalReference
{
    public CanonicalReference(CanonicalEntityKind kind, Guid id) { Kind = Require.Defined(kind); Id = Require.Id(id); }
    public CanonicalEntityKind Kind { get; }
    public Guid Id { get; }
}
