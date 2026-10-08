using BetStats.Application.Coverage;
using BetStats.Application.Evaluation;
using BetStats.Domain.Coverage;
using BetStats.Domain.Governance;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;

namespace BetStats.UnitTests;

public sealed class HistoricalIntegrityRulesTests
{
    private static readonly DateTime T = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
    private static CoverageScope Scope(int start, int end) => new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
        "football-match", ObservationType.EventDate, "fiction", "2026", new(IntervalKind.Calendar, null, null, new(2026, 1, start), new(2026, 1, end), "UTC-calendar"));
    private static CoverageItem Item(CoverageScope scope, CoverageStatus status, Guid[] ids) => new(new()
    {
        Id = Guid.NewGuid(),
        SourceId = scope.SourceId,
        Scope = scope,
        Claim = status,
        Basis = CoverageBasis.OwnedFixtureInventory,
        EvidenceReference = "owned",
        RawId = Guid.NewGuid(),
        RawHash = new('a', 64),
        PolicyId = Guid.NewGuid(),
        SupportingObservationIds = ids,
        Version = 1,
        RetrievedAtUtc = T,
        AvailableAtUtc = T,
        ValidUntilUtc = T.AddDays(30),
        OperatorId = "audit",
        Reason = "Fictional probe"
    }, null, status, [], [], [], ids.Select((id, i) => new CoverageFact(id, DateOnly.FromDayNumber(scope.Interval.StartDate!.Value.DayNumber + i), null)).ToArray());
    [Fact]
    public void Accepts_consistent_overlapping_inventories()
    {
        var a = Scope(1, 10); var b = a with { Interval = Scope(5, 15).Interval }; var x = Guid.NewGuid(); var y = Guid.NewGuid(); var z = Guid.NewGuid();
        // x is on Jan 2, y on Jan 7, z on Jan 12. Both exhaustive inventories agree on the overlap.
        var status = CoverageRules.Classify(a.Interval, [Item(a, CoverageStatus.VerifiedComplete, [x, y]) with { Facts = [new(x, new(2026, 1, 2), null), new(y, new(2026, 1, 7), null)] }, Item(b, CoverageStatus.VerifiedComplete, [y, z]) with { Facts = [new(y, new(2026, 1, 7), null), new(z, new(2026, 1, 12), null)] }]);
        Assert.Equal(CoverageStatus.VerifiedComplete, status);
    }
    [Fact]
    public void Accepts_wide_nonempty_and_empty_subinterval()
    {
        var a = Scope(1, 10); var b = a with { Interval = Scope(5, 8).Interval };
        // The only event is Jan 2; the subinterval Jan 5-8 is genuinely empty.
        Assert.Equal(CoverageStatus.VerifiedComplete, CoverageRules.Classify(a.Interval, [Item(a, CoverageStatus.VerifiedComplete, [Guid.NewGuid()]), Item(b, CoverageStatus.VerifiedEmpty, [])]));
    }
    [Fact]
    public void Rejects_window_length_check_accepting_lookahead_interval()
    {
        var scope = Scope(15, 20); var query = new CoverageQuery(scope, T, DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new());
        var report = new CoverageReport(query, CoverageStatus.VerifiedComplete, true, [Item(scope, CoverageStatus.VerifiedComplete, [Guid.NewGuid()])], [], [], [], [], []);
        var requirement = new FeatureCoverageRequirement("complete", 1, [ObservationType.EventDate], 5, false, "Completed", 1, false, true);
        Assert.Throws<ArgumentException>(() => CoverageRules.Gate(requirement, [report]));
    }
    [Fact]
    public void Rejects_evaluation_accepting_missing_kickoff_and_unchecked_horizon()
    {
        var requirement = new FeatureCoverageRequirement("complete", 1, [ObservationType.EventDate], 5, false, "Completed", 1, false, true);
        var definition = new EvaluationDefinition(1, Guid.NewGuid(), EvaluationTarget.BothTeamsScoring, DatasetMode.HistoricalAsKnown, null,
            PredictionCutoffPolicy.BeforeJustifiedKickoff, TimeSpan.FromMinutes(1), ["features", "event-time", "quality", "coverage", "source-policy"], "BothTeamsScoring", 1, requirement, [EvaluationContracts.Metrics[1]]);
        var label = new OutcomeAvailability(Guid.NewGuid(), T.AddHours(1), T.AddHours(2), 1);
        Assert.False(EvaluationContracts.Eligibility(definition, T, T, T, label, T.AddDays(1), true, new("complete", FeatureCoverageOutcome.Eligible, [], [], [])).Eligible);
        // Missing target event evidence must never establish kickoff eligibility.
    }
}
