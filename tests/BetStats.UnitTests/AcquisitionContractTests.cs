using BetStats.Application.Datasets;
using BetStats.Application.Pipeline;
using BetStats.Application.Providers;
using BetStats.Domain.Coverage;

namespace BetStats.UnitTests;

public sealed class AcquisitionContractTests
{
    private static AcquisitionPlan Plan() => new(1, new(1, Guid.NewGuid(), "controlled-local", Guid.NewGuid(), 1, FootballTransport.LocalCsv,
        "football-history-v1", [AcquisitionCategory.PrematchMetadata]), new("FICT", "2026"), AcquisitionCategory.PrematchMetadata, new string('a', 64), 20, ValidationEvidenceKind.Synthetic);
    [Theory] [InlineData("version")] [InlineData("source")] [InlineData("policy")] [InlineData("bytes")] [InlineData("pages")]
    [InlineData("profile")] [InlineData("provider")] [InlineData("capability")] [InlineData("scope")] [InlineData("hash")]
    [InlineData("identity")]
    public void Unsupported_plan_or_capability_fails_closed(string value)
    {
        var p = Plan(); p = value switch { "version" => p with { Version = 2 }, "source" => p with { Capability = p.Capability with { SourceId = Guid.Empty } },
            "policy" => p with { Capability = p.Capability with { PolicyVersion = 0 } }, "bytes" => p with { Length = 1048577 },
            "pages" => p with { Capability = p.Capability with { MaximumPages = 2 } }, "profile" => p with { Capability = p.Capability with { Profile = "guess" } },
            "provider" => p with { Capability = p.Capability with { Provider = "secret://invalid" } }, "capability" => p with { Category = AcquisitionCategory.PostmatchResults },
            "scope" => p with { Scope = new("", "") }, "hash" => p with { Hash = "bad" }, _ => p with { DateObservationId = Guid.NewGuid() } };
        Assert.Throws<ArgumentException>(p.Validate);
    }
    [Fact] public void Policy_scope_and_origin_are_fingerprinted_without_changing_legacy_enum_values()
    {
        var p = Plan(); p.Validate();
        Assert.NotEqual(p.Fingerprint, (p with { Capability = p.Capability with { PolicyVersion = 2 } }).Fingerprint);
        Assert.NotEqual(p.Fingerprint, (p with { EvidenceKind = ValidationEvidenceKind.OperatorDeclaredReal }).Fingerprint);
        Assert.Equal(3, (int)PipelineKind.FootballValidation); Assert.Equal(0, (int)PipelineKind.LocalSynchronization);
        Assert.DoesNotContain("Fingerprint", System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(p)));
        Assert.Equal("{\"Cursor\":null,\"PageSize\":10}", System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(new FootballApiPageRequest(null, 10))));
    }
    private static AcquisitionObservation Receipt(DateTime cutoff) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), DateOnly.FromDateTime(cutoff.AddHours(2)), cutoff.AddMinutes(-2), cutoff.AddMinutes(-1), null,
        cutoff.AddHours(2), EventTimePrecision.Second, Guid.NewGuid(), cutoff.AddSeconds(-1), cutoff.AddSeconds(-2), null, false, false, []);
    [Theory] [InlineData("observation")] [InlineData("available")] [InlineData("kickoffRecorded")] [InlineData("kickoffAvailable")]
    [InlineData("unknown")] [InlineData("identity")] [InlineData("kickoff")] [InlineData("exclusion")]
    public void Prospective_gate_rejects_future_or_missing_evidence(string problem)
    {
        var t = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc); var r = Receipt(t); Assert.True(ProspectiveEvidenceRules.KnownBefore(r, t));
        r = problem switch { "observation" => r with { RecordedUtc = t }, "available" => r with { AvailableUtc = t.AddSeconds(1) },
            "kickoffRecorded" => r with { KickoffRecordedUtc = t }, "kickoffAvailable" => r with { KickoffAvailableUtc = t.AddSeconds(1) },
            "unknown" => r with { Precision = EventTimePrecision.Unknown }, "identity" => r with { IdentityDecisionId = Guid.Empty },
            "kickoff" => r with { KickoffUtc = t }, _ => r with { Exclusions = ["missing_history"] } };
        Assert.False(ProspectiveEvidenceRules.KnownBefore(r, t));
    }
    [Fact] public void Unknown_origin_and_coverage_do_not_become_independent_certification()
    {
        var p = Plan() with { EvidenceKind = ValidationEvidenceKind.Unknown }; p.Validate();
        var c = new ProspectiveCohortInput(1, p.Capability.SourceId, [Guid.NewGuid()], [], []); c.Validate();
        Assert.Throws<ArgumentException>(() => (c with { AcquisitionExecutions = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToArray() }).Validate());
        Assert.DoesNotContain("IndependentlyVerified", Enum.GetNames<ValidationEvidenceKind>());
    }
}
