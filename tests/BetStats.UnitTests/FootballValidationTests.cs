using BetStats.Application.Pipeline;
using BetStats.Application.Datasets;

namespace BetStats.UnitTests;

public sealed class FootballValidationTests
{
    private static FootballValidationInput Input() => new(1, Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), "football-history-v1",
        new("FICT", "2026"), ValidationEvidenceKind.Synthetic, []);
    [Theory] [InlineData("football-history-v1")] [InlineData("results-v1")] [InlineData("metadata-v1")]
    public void Only_existing_versioned_profiles_are_supported(string profile) => (Input() with { Profile = profile }).Validate();
    [Theory] [InlineData("profile")] [InlineData("hash")] [InlineData("version")] [InlineData("source")] [InlineData("raw")]
    [InlineData("scope")] [InlineData("kind")] [InlineData("bound")] [InlineData("duplicate")]
    public void Unknown_or_ambiguous_inputs_fail_closed(string invalid)
    {
        var p = Input(); var reference = new ValidationPublication(Guid.NewGuid());
        p = invalid switch { "profile" => p with { Profile = "guess" }, "hash" => p with { ExpectedRawHash = "bad" }, "version" => p with { Version = 2 },
            "source" => p with { SourceId = Guid.Empty }, "raw" => p with { RawId = Guid.Empty }, "scope" => p with { Scope = new("", "") },
            "kind" => p with { EvidenceKind = (ValidationEvidenceKind)99 }, "bound" => p with { Publications = Enumerable.Range(0, 4).Select(_ => new ValidationPublication(Guid.NewGuid())).ToArray() },
            _ => p with { Publications = [reference, reference] } };
        Assert.Throws<ArgumentException>(p.Validate);
    }
    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0, ValidationReadiness.Unavailable)]
    [InlineData(3, 3, 3, 0, 0, 0, ValidationReadiness.Incomplete)]
    [InlineData(3, 3, 3, 0, 1, 0, ValidationReadiness.Incomplete)]
    [InlineData(3, 2, 2, 0, 1, 1, ValidationReadiness.Incomplete)]
    [InlineData(3, 3, 3, 1, 1, 1, ValidationReadiness.Blocked)]
    [InlineData(3, 3, 3, 0, 1, 1, ValidationReadiness.Verified)]
    public void Readiness_never_substitutes_import_success_for_eligible_evaluation(int parsed, int imported, int identities, int issues, int models, int eligible, ValidationReadiness expected) =>
        Assert.Equal(expected, FootballValidationRules.Readiness(parsed, imported, identities, issues, models, eligible));
    [Fact] public void Evidence_classification_is_part_of_immutable_input_fingerprint()
    {
        var p = Input(); Assert.NotEqual(CanonicalDatasetJson.Fingerprint(p), CanonicalDatasetJson.Fingerprint(p with { EvidenceKind = ValidationEvidenceKind.OperatorDeclaredReal }));
    }
    [Fact] public void Existing_pipeline_kind_numeric_contract_is_preserved()
    {
        Assert.Equal(0, (int)PipelineKind.LocalSynchronization); Assert.Equal(1, (int)PipelineKind.PrematchPrediction); Assert.Equal(2, (int)PipelineKind.PostmatchEvaluation);
    }
}
