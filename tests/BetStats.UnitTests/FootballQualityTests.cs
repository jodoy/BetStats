using BetStats.Application.Quality;
using BetStats.Domain.Quality;
using BetStats.Domain.Sports;

namespace BetStats.UnitTests;

public sealed class FootballQualityTests
{
    private static readonly Guid Competition = Guid.Parse("9cce1b85-037b-4a3e-a74c-12ece3f4a402");
    private static readonly Guid Home = Guid.Parse("9cce1b85-037b-4a3e-a74c-12ece3f4a403");
    private static readonly Guid Away = Guid.Parse("9cce1b85-037b-4a3e-a74c-12ece3f4a404");
    private static FootballQualityEvidence Valid => new(FootballQualityRules.Football, FootballQualityRules.Football, Competition, Competition,
        new(2026, 1, 2), new(2026, 1, 1), new(2026, 12, 31), Home, Away, FootballQualityRules.Football, FootballQualityRules.Football,
        false, false, false, null, null, SportingEventStatus.Completed, true, true, true);
    [Fact]
    public void Catalog_is_stable_versioned_and_complete_for_valid_metadata()
    {
        var first = FootballQualityRules.Assess(Valid); Assert.Equal(first, FootballQualityRules.Assess(Valid));
        Assert.Equal(FootballQualityRules.Catalog, first.Select(i => i.Rule));
        Assert.All(first, i => { Assert.True(i.Passed); Assert.False(i.BlocksEligibility); Assert.Equal(1, i.Rule.Version); });
    }
    [Theory]
    [InlineData("sport")][InlineData("competition-season")][InlineData("event-date")][InlineData("season-interval")]
    [InlineData("different-participants")][InlineData("participant-sport")][InlineData("canonical-identity")][InlineData("event-collision")]
    [InlineData("home-away")][InlineData("event-status")][InlineData("date-conflict")][InlineData("correction-chain")][InlineData("provenance")][InlineData("replay")][InlineData("cross-source")]
    public void Every_rule_has_a_deterministic_blocking_counterexample(string rule)
    {
        var input = rule switch
        {
            "sport" => Valid with { SportId = Away },
            "competition-season" => Valid with { SeasonCompetitionId = Away },
            "event-date" => Valid with { Date = default },
            "season-interval" => Valid with { Date = new(2027, 1, 2) },
            "different-participants" => Valid with { AwayId = Home },
            "participant-sport" => Valid with { HomeSportId = Away },
            "canonical-identity" => Valid with { IdentityMissing = true },
            "event-collision" => Valid with { CanonicalMismatch = true },
            "home-away" => Valid with { ConflictingAssignments = true },
            "event-status" => Valid with { PreviousStatus = SportingEventStatus.Completed, Status = SportingEventStatus.Scheduled },
            "date-conflict" => Valid with { PreviousDate = new(2026, 1, 1), LaterEvidence = false },
            "correction-chain" => Valid with { ChainValid = false },
            "provenance" => Valid with { ProvenanceValid = false },
            "cross-source" => Valid with { IndependentSourceConflict = true },
            _ => Valid with { StaleReplay = true }
        };
        var issue = Assert.Single(FootballQualityRules.Assess(input), i => i.Rule.Id == "football." + rule);
        Assert.False(issue.Passed); Assert.True(issue.BlocksEligibility); Assert.NotEqual("passed", issue.ReasonCode);
    }
    [Fact]
    public void Later_date_correction_is_distinct_from_simultaneous_conflict_and_invalid_transition()
    {
        var later = Valid with { PreviousDate = new(2026, 1, 1) };
        var result = FootballQualityRules.Assess(later).Single(i => i.Rule.Id == "football.date-conflict");
        Assert.True(result.Passed); Assert.Equal(QualityClassification.HistoricalCorrection, result.Classification);
        Assert.Equal(QualityClassification.ObservationConflict, FootballQualityRules.Assess(later with { SimultaneousContradiction = true }).Single(i => i.Rule.Id == result.Rule.Id).Classification);
        Assert.True(FootballQualityRules.Assess(Valid with { PreviousStatus = SportingEventStatus.Postponed, Status = SportingEventStatus.Completed }).Single(i => i.Rule.Id == "football.event-status").Passed);
    }
    [Fact]
    public void Unknown_interval_does_not_invent_dates_and_severity_is_not_eligibility()
    {
        Assert.True(FootballQualityRules.Assess(Valid with { SeasonStart = null, SeasonEnd = null }).Single(i => i.Rule.Id == "football.season-interval").Passed);
        var unresolved = FootballQualityRules.Assess(Valid with { IdentityMissing = true }).Single(i => i.Rule.Id == "football.canonical-identity");
        Assert.Equal(QualitySeverity.Warning, unresolved.Severity); Assert.True(unresolved.BlocksEligibility);
        var informational = new QualityIssue(new("example", 1), false, QualitySeverity.Warning, false, "optional_evidence_absent");
        Assert.False(informational.BlocksEligibility);
    }
    [Theory]
    [InlineData("duplicate_reference", QualityClassification.DuplicateEquivalent)]
    [InlineData("identity_collision", QualityClassification.DuplicateContradictory)]
    [InlineData("invalid_date", QualityClassification.Invalid)]
    public void Source_validation_preserves_duplicate_conflict_distinction(string code, QualityClassification expected) =>
        Assert.Equal(expected, FootballQualityRules.ParseIssue(code).Classification);
}
