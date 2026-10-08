using BetStats.Application.Coverage;
using BetStats.Domain.Coverage;
using BetStats.Domain.Observations;

namespace BetStats.UnitTests;

public sealed class CoverageIntersectionRegressionTests
{
    private static readonly DateTime T = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static CoverageInterval Interval(int start, int end, bool utc) => utc ? new(IntervalKind.Utc, T.AddDays(start), T.AddDays(end), null, null, null)
        : new(IntervalKind.Calendar, null, null, DateOnly.FromDateTime(T.AddDays(start)), DateOnly.FromDateTime(T.AddDays(end)), "UTC-calendar");
    private static CoverageFact Fact(Guid id, int day, bool utc) => utc ? new(id, null, T.AddDays(day)) : new(id, DateOnly.FromDateTime(T.AddDays(day)), null);
    private static CoverageItem Item(CoverageInterval interval, CoverageStatus status, params CoverageFact[] facts) => new(new()
    {
        Id = Guid.NewGuid(),
        SourceId = Guid.NewGuid(),
        Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "football-match", ObservationType.EventDate, "fiction", "2026", interval),
        Claim = status,
        Basis = CoverageBasis.OwnedFixtureInventory,
        EvidenceReference = "owned",
        RawId = Guid.NewGuid(),
        RawHash = new('a', 64),
        PolicyId = Guid.NewGuid(),
        SupportingObservationIds = facts.Select(f => f.ObservationId).ToArray(),
        Version = 1,
        RetrievedAtUtc = T,
        AvailableAtUtc = T,
        ValidUntilUtc = T.AddDays(30),
        OperatorId = "test",
        Reason = "Fictional interval proof"
    }, null, status, [], [], [], facts);
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Different_extents_compare_only_the_intersection(bool utc)
    {
        var x = Guid.NewGuid(); var y = Guid.NewGuid(); var z = Guid.NewGuid();
        var a = Item(Interval(0, 10, utc), CoverageStatus.VerifiedComplete, Fact(x, 2, utc), Fact(y, 7, utc));
        var b = Item(Interval(5, 15, utc), CoverageStatus.VerifiedComplete, Fact(y, 7, utc), Fact(z, 12, utc));
        Assert.Null(CoverageRules.Conflict(a, b)); Assert.Equal(CoverageStatus.VerifiedComplete, CoverageRules.Classify(Interval(0, 15, utc), [a, b]));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Empty_subinterval_excludes_an_event_on_its_end_boundary(bool utc)
    {
        var a = Item(Interval(0, 10, utc), CoverageStatus.VerifiedComplete, Fact(Guid.NewGuid(), 8, utc));
        var b = Item(Interval(5, 8, utc), CoverageStatus.VerifiedEmpty);
        Assert.Null(CoverageRules.Conflict(a, b));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Different_facts_inside_overlap_are_a_real_conflict(bool utc)
    {
        var a = Item(Interval(0, 10, utc), CoverageStatus.VerifiedComplete, Fact(Guid.NewGuid(), 7, utc));
        var b = Item(Interval(5, 15, utc), CoverageStatus.VerifiedComplete, Fact(Guid.NewGuid(), 7, utc));
        Assert.Equal(Interval(5, 10, utc), CoverageRules.Conflict(a, b));
        Assert.Equal(CoverageStatus.Conflicting, CoverageRules.Classify(Interval(0, 15, utc), [a, b]));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Adjacent_disjoint_inventory_never_conflicts(bool utc) => Assert.Null(CoverageRules.Conflict(
        Item(Interval(0, 5, utc), CoverageStatus.VerifiedComplete, Fact(Guid.NewGuid(), 2, utc)), Item(Interval(5, 10, utc), CoverageStatus.VerifiedEmpty)));
}
