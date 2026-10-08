using System.Text;
using BetStats.Application.Datasets;
using BetStats.Domain.Governance;
using BetStats.Domain.Quality;

namespace BetStats.UnitTests;

public sealed class DatasetTests
{
    private static readonly DateTime Cutoff = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Home = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Away = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static DatasetEvidenceReference Fact(int day, string? status = "Completed", Guid? eventId = null) =>
        new(Guid.Empty, eventId ?? Guid.NewGuid(), Home, Away, Guid.Empty, [], [], Guid.NewGuid(), status is null ? null : Guid.NewGuid(),
            Guid.Empty, new string('a', 64), Cutoff.AddDays(-1), Cutoff.AddDays(-1), Cutoff.AddDays(-1), new(2026, 10, day), status, [], [], Cutoff, Cutoff, []);
    private static DatasetDefinition Definition() => new(1, Home, Away, Home, "FICT", "2026-fiction", new(2026, 1, 1), new(2026, 12, 31),
        Cutoff, DatasetMode.HistoricalAsKnown, null, 1, 1, DataPurpose.InternalAnalytics, new(), "UTC-calendar", "eligible-observed-metadata-v1", "fail-closed-v1", [new(Home, Cutoff)]);
    [Theory]
    [InlineData("mode")][InlineData("reconstruction")][InlineData("time")][InlineData("version")][InlineData("scope")][InlineData("targets")][InlineData("rules")]
    public void Invalid_or_ambiguous_definitions_fail_closed(string invalid)
    {
        var d = Definition();
        d = invalid switch {
            "mode" => d with { Mode = (DatasetMode)999 }, "reconstruction" => d with { Mode = DatasetMode.RetrospectiveReconstruction },
            "time" => d with { AsOfUtc = DateTime.SpecifyKind(Cutoff, DateTimeKind.Unspecified) }, "version" => d with { FeatureSchemaVersion = 2 },
            "scope" => d with { SeasonEnd = d.SeasonStart.AddDays(-1) }, "targets" => d with { Targets = [] }, _ => d with { ExclusionRule = "ignore-denials" } };
        Assert.Throws<ArgumentException>(d.Validate);
    }
    [Fact]
    public void Explicit_modes_and_separate_cutoffs_validate()
    {
        Definition().Validate();
        (Definition() with { Mode = DatasetMode.RetrospectiveReconstruction, ReconstructionAtUtc = Cutoff.AddDays(1) }).Validate();
        Assert.Throws<ArgumentException>((Definition() with { ReconstructionAtUtc = Cutoff }).Validate);
        Assert.Throws<ArgumentException>((Definition() with { Targets = [new(Home, Cutoff.AddDays(1))] }).Validate);
    }
    [Fact]
    public void Canonical_serialization_normalizes_text_keys_null_and_utc()
    {
        var a = new Dictionary<string, object?> { ["z"] = Cutoff, ["a"] = "e\u0301", ["n"] = null };
        var b = new Dictionary<string, object?> { ["n"] = null, ["a"] = "é", ["z"] = Cutoff };
        Assert.Equal(CanonicalDatasetJson.Serialize(a), CanonicalDatasetJson.Serialize(b));
        var json = Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(a));
        Assert.Contains("\"n\":null", json); Assert.Contains("2026-10-08T12:00:00.000000Z", json);
        Assert.Equal(Cutoff, CanonicalDatasetJson.Deserialize<DateTime>(CanonicalDatasetJson.Serialize(Cutoff)));
    }
    [Fact]
    public void Only_prior_completed_supported_observations_contribute()
    {
        var target = Fact(9);
        var eligible = Fact(3);
        var history = new[] { eligible, Fact(8), Fact(9), Fact(10), target, Fact(4, "Cancelled"), Fact(5, "Scheduled") };
        var vector = FootballMetadataFeatures.Compute(target, Cutoff, history);
        Assert.Equal(4, vector.Values.Count);
        Assert.All(vector.Values, v => Assert.Null(v.MissingReason));
        Assert.Equal(1, vector.Values[1].Value); Assert.Equal(6, vector.Values[0].Value);
        Assert.All(vector.Values, v => Assert.Contains(eligible.DateObservationId, v.EvidenceIds));
        Assert.Equal(CanonicalDatasetJson.Fingerprint(vector), CanonicalDatasetJson.Fingerprint(FootballMetadataFeatures.Compute(target, Cutoff, history.Reverse().ToArray())));
    }
    [Theory]
    [InlineData("status")][InlineData("empty")][InlineData("late-recorded")][InlineData("late-raw")][InlineData("late-available")]
    public void Missing_or_future_evidence_never_becomes_zero(string problem)
    {
        var fact = Fact(3); var target = Fact(9);
        fact = problem switch { "status" => fact with { Status = null, StatusObservationId = null }, "late-recorded" => fact with { DateRecordedUtc = Cutoff.AddSeconds(1) },
            "late-raw" => fact with { RawRecordedUtc = Cutoff.AddSeconds(1) }, "late-available" => fact with { DateAvailableUtc = Cutoff.AddSeconds(1) }, _ => fact };
        Assert.All(FootballMetadataFeatures.Compute(target, Cutoff, problem == "empty" ? [] : [fact]).Values, v => { Assert.Null(v.Value); Assert.NotNull(v.MissingReason); });
    }
    [Fact]
    public void Evidence_and_feature_version_changes_change_fingerprints()
    {
        var target = Fact(9); var history = new[] { Fact(3) }; var vector = FootballMetadataFeatures.Compute(target, Cutoff, history);
        var frozen = new FeatureArtifact(1, target, history, vector);
        Assert.NotEqual(CanonicalDatasetJson.Fingerprint(frozen), CanonicalDatasetJson.Fingerprint(frozen with { History = [history[0] with { RawHash = new string('b', 64) }] }));
        Assert.NotEqual(CanonicalDatasetJson.Fingerprint(vector), CanonicalDatasetJson.Fingerprint(vector with { SchemaVersion = 2 }));
        Assert.All(FootballMetadataFeatures.Catalog, d => { Assert.Equal(1, d.Version); Assert.NotEmpty(d.RequiredEvidence); Assert.Contains("strictly prior UTC calendar day", d.TemporalBehavior); Assert.Equal("unavailable", d.MissingSemantics); });
    }
}
