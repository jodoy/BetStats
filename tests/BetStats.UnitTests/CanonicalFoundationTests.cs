using BetStats.Application.Observations;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Sports;

namespace BetStats.UnitTests;

public sealed class CanonicalFoundationTests
{
    private static readonly DateTime Time = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static Competition Competition(Guid? sport = null) => new(Guid.NewGuid(), sport ?? ReferenceSports.All[0].Id, "Synthetic competition", null, CompetitionType.Tournament);
    private static ProviderIdentity Identity(CanonicalEntityKind kind = CanonicalEntityKind.Participant) => new(Guid.NewGuid(), Guid.NewGuid(), kind, "synthetic-id", Time);

    [Theory]
    [InlineData("")]
    [InlineData("Football")]
    [InlineData("ice hockey")]
    [InlineData("football\n")]
    public void Sport_codes_are_explicit_lowercase_identifiers(string code) =>
        Assert.Throws<ArgumentException>(() => new Sport(Guid.NewGuid(), code, "Synthetic"));

    [Fact]
    public void Reference_sports_have_stable_unique_canonical_ids()
    {
        Assert.Equal(new[] { "football", "tennis", "basketball", "ice-hockey" }, ReferenceSports.All.Select(s => s.Code));
        Assert.Equal(4, ReferenceSports.All.Select(s => s.Id).Distinct().Count());
        Assert.Equal(ReferenceSports.All.Select(s => s.Id), ReferenceSports.All.Select(s => s.Id));
        Assert.Throws<ArgumentException>(() => new Participant(Guid.NewGuid(), ReferenceSports.All[0].Id, "Synthetic", (ParticipantType)99));
    }

    [Theory]
    [InlineData(ParticipantType.Team)]
    [InlineData(ParticipantType.Individual)]
    public void Tennis_supports_individuals_and_teams_without_home_away(ParticipantType type)
    {
        var competition = Competition(ReferenceSports.All[1].Id);
        var sportingEvent = new SportingEvent(Guid.NewGuid(), competition, null, null, SportingEventStatus.Scheduled, Time);
        sportingEvent.AddParticipant(new Participant(Guid.NewGuid(), competition.SportId, "Synthetic first", type), ParticipantRole.Side1, 1);
        sportingEvent.AddParticipant(new Participant(Guid.NewGuid(), competition.SportId, "Synthetic second", type), ParticipantRole.Side2, 2);
        Assert.Equal(2, sportingEvent.Participants.Count);
    }

    [Fact]
    public void Season_dates_and_event_competition_must_agree()
    {
        var competition = Competition();
        Assert.Throws<ArgumentException>(() => new Season(Guid.NewGuid(), competition.Id, "Synthetic", new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)));
        var otherSeason = new Season(Guid.NewGuid(), Guid.NewGuid(), "Synthetic", null, null);
        Assert.Throws<ArgumentException>(() => new SportingEvent(Guid.NewGuid(), competition, otherSeason, null, SportingEventStatus.Scheduled, Time));
        Assert.Throws<ArgumentException>(() => new Competition(Guid.NewGuid(), competition.SportId, "Synthetic", "pl", CompetitionType.League));
    }

    [Fact]
    public void Membership_rejects_cross_sport_duplicates_and_invalid_slots()
    {
        var competition = Competition();
        var sportingEvent = new SportingEvent(Guid.NewGuid(), competition, null, null, SportingEventStatus.Scheduled, Time);
        var first = new Participant(Guid.NewGuid(), competition.SportId, "Synthetic first", ParticipantType.Team);
        var second = new Participant(Guid.NewGuid(), competition.SportId, "Synthetic second", ParticipantType.Team);
        Assert.Throws<ArgumentException>(() => sportingEvent.AddParticipant(new Participant(Guid.NewGuid(), ReferenceSports.All[1].Id, "Synthetic", ParticipantType.Individual), ParticipantRole.Side1, 1));
        Assert.Throws<ArgumentException>(() => sportingEvent.AddParticipant(first, ParticipantRole.Away, 1));
        sportingEvent.AddParticipant(first, ParticipantRole.Home, 1);
        Assert.Throws<ArgumentException>(() => sportingEvent.AddParticipant(first, ParticipantRole.Away, 2));
        Assert.Throws<ArgumentException>(() => sportingEvent.AddParticipant(second, ParticipantRole.Home, 1));
        Assert.Throws<ArgumentException>(() => sportingEvent.AddParticipant(second, ParticipantRole.Side2, 3));
    }

    [Theory]
    [InlineData(ResolutionStatus.Unresolved)]
    [InlineData(ResolutionStatus.Ambiguous)]
    public void Uncertain_decisions_cannot_select_a_canonical_target(ResolutionStatus status)
    {
        var identity = Identity();
        var decision = new IdentityResolution(Guid.NewGuid(), identity, status, null, "synthetic-reviewer", "Needs evidence", Time);
        Assert.Null(decision.CanonicalId);
        Assert.Throws<ArgumentException>(() => new IdentityResolution(Guid.NewGuid(), identity, status, new(CanonicalEntityKind.Participant, Guid.NewGuid()), "synthetic-reviewer", "Invalid guess", Time));
    }

    [Fact]
    public void Resolution_requires_matching_target_and_audited_predecessor()
    {
        var identity = Identity();
        Assert.Throws<ArgumentException>(() => new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Resolved, null, "reviewer", "Evidence", Time));
        Assert.Throws<ArgumentException>(() => new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Resolved, new(CanonicalEntityKind.Sport, Guid.NewGuid()), "reviewer", "Evidence", Time));
        var previous = new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Unresolved, null, "reviewer", "No evidence", Time);
        var next = new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Resolved, new(CanonicalEntityKind.Participant, Guid.NewGuid()), "reviewer", "Verified evidence", Time.AddHours(1), previous);
        Assert.Equal(2, next.Version);
        Assert.Equal(previous.Id, next.PreviousDecisionId);
        Assert.Throws<ArgumentException>(() => new IdentityResolution(Guid.NewGuid(), Identity(), ResolutionStatus.Unresolved, null, "reviewer", "Evidence", Time, previous));
    }

    [Fact]
    public void Observation_types_and_conservative_availability_are_enforced()
    {
        var identity = Identity();
        Assert.Throws<ArgumentException>(() => new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time, Time.AddSeconds(-1), Time, textValue: "Synthetic"));
        Assert.Throws<ArgumentException>(() => new Observation(Guid.NewGuid(), identity, null, ObservationType.ScheduledStart, Time, Time, Time, timestampValueUtc: Time));
        Assert.Throws<ArgumentException>(() => new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time, Time, Time, textValue: "Synthetic", statusValue: SportingEventStatus.Scheduled));
        var observation = new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time, Time, Time, textValue: "Synthetic", sourceEventTimeUtc: Time.AddDays(-10), sourcePublishedAtUtc: Time.AddDays(-1));
        Assert.Equal(Time, observation.AvailableAtUtc);
        Assert.Null(observation.CanonicalId);
    }

    [Fact]
    public void Corrections_preserve_original_and_cannot_backdate_or_change_stream()
    {
        var identity = Identity();
        var original = new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time, Time.AddHours(1), Time, textValue: "Original");
        var correction = new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time.AddHours(2), Time.AddHours(2), Time.AddHours(2), textValue: "Corrected", corrects: original);
        Assert.Equal("Original", original.TextValue);
        Assert.Equal(original.Id, correction.CorrectsObservationId);
        Assert.Equal(2, correction.Version);
        Assert.Throws<ArgumentException>(() => new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time, Time, Time, textValue: "Backdated", corrects: original));
        Assert.Throws<ArgumentException>(() => new Observation(Guid.NewGuid(), Identity(), null, ObservationType.DisplayName, Time, Time.AddHours(2), Time, textValue: "Other stream", corrects: original));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Domain_and_query_reject_non_utc(DateTimeKind kind)
    {
        var invalid = DateTime.SpecifyKind(Time, kind);
        Assert.Throws<ArgumentException>(() => new ProviderIdentity(Guid.NewGuid(), Guid.NewGuid(), CanonicalEntityKind.Sport, "synthetic", invalid));
        Assert.Throws<ArgumentException>(() => new Observation(Guid.NewGuid(), Identity(), null, ObservationType.DisplayName, invalid, Time, Time, textValue: "Synthetic"));
        Assert.Throws<ArgumentException>(() => new ObservationQuery(CanonicalEntityKind.Sport, invalid));
    }

    [Fact]
    public void Query_and_domain_reject_precision_loss_and_empty_ids()
    {
        Assert.Throws<ArgumentException>(() => new ObservationQuery(CanonicalEntityKind.Sport, Time.AddTicks(1)));
        Assert.Throws<ArgumentException>(() => new ProviderIdentity(Guid.NewGuid(), Guid.NewGuid(), CanonicalEntityKind.Sport, "synthetic", Time.AddTicks(1)));
        Assert.Throws<ArgumentException>(() => new ObservationQuery(CanonicalEntityKind.Sport, Time, Guid.Empty));
        Assert.Throws<ArgumentException>(() => new CanonicalReference(CanonicalEntityKind.Sport, Guid.Empty));
    }
}
