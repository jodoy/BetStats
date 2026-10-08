using BetStats.Domain.Identity;
using BetStats.Domain.Observations;

namespace BetStats.UnitTests;

public sealed class FootballObservationTests
{
    private static readonly DateTime Time = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    [Fact]
    public void Event_date_preserves_calendar_date_without_fabricating_utc_kickoff()
    {
        var identity = new ProviderIdentity(Guid.NewGuid(), Guid.NewGuid(), CanonicalEntityKind.SportingEvent, "synthetic", Time);
        var original = new Observation(Guid.NewGuid(), identity, null, ObservationType.EventDate, Time, Time, Time, dateValue: new(2020, 2, 3));
        var corrected = new Observation(Guid.NewGuid(), identity, null, ObservationType.EventDate, Time, Time, Time, corrects: original, dateValue: new(2020, 2, 4));
        Assert.Null(original.TimestampValueUtc); Assert.Null(original.SourceEventTimeUtc);
        Assert.Equal(original.Id, corrected.CorrectsObservationId); Assert.Equal(2, corrected.Version);
        Assert.Equal(new DateOnly(2020, 2, 3), original.DateValue);
    }
    [Fact]
    public void Event_date_rejects_wrong_kind_missing_value_and_mixed_value_types()
    {
        var team = new ProviderIdentity(Guid.NewGuid(), Guid.NewGuid(), CanonicalEntityKind.Participant, "synthetic", Time);
        var sportingEvent = new ProviderIdentity(Guid.NewGuid(), Guid.NewGuid(), CanonicalEntityKind.SportingEvent, "synthetic", Time);
        Assert.Throws<ArgumentException>(() => new Observation(Guid.NewGuid(), team, null, ObservationType.EventDate, Time, Time, Time, dateValue: new(2020, 2, 3)));
        Assert.Throws<ArgumentException>(() => new Observation(Guid.NewGuid(), sportingEvent, null, ObservationType.EventDate, Time, Time, Time));
        Assert.Throws<ArgumentException>(() => new Observation(Guid.NewGuid(), sportingEvent, null, ObservationType.EventDate, Time, Time, Time, textValue: "mixed", dateValue: new(2020, 2, 3)));
    }
}
