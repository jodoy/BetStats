using BetStats.Application.Pipeline;
using BetStats.Application.Providers;

namespace BetStats.UnitTests;

public sealed class ProviderActivationGrantTests
{
    private static ProviderActivationGrant Valid() => new(1, Guid.NewGuid(), Guid.NewGuid(), "api-football", Guid.NewGuid(), 1,
        new(1, 39, 2026, new(2026,10,14), new(2026,10,20)), AcquisitionCategory.PrematchMetadata,
        "operator:permission-document", "operator:account-entitlement", "operator:reviewer", new(2026,10,21,0,0,0,DateTimeKind.Utc),
        5, 60, 30, true, true, ValidationEvidenceKind.OperatorDeclaredReal, true);
    [Theory] [InlineData("permission")] [InlineData("entitlement")] [InlineData("reviewer")]
    [InlineData("retention")] [InlineData("attribution")] [InlineData("budget")] [InlineData("interval")]
    [InlineData("timeout")] [InlineData("window")] [InlineData("category")] [InlineData("clock")] [InlineData("payload")] [InlineData("provider")]
    public void Missing_or_unsupported_grant_conditions_fail_closed(string fault)
    {
        var g = Valid(); g = fault switch {
            "permission" => g with { PermissionEvidence = "" }, "entitlement" => g with { EntitlementEvidence = "" },
            "reviewer" => g with { Reviewer = "" }, "retention" => g with { DurableRetentionApproved = false },
            "attribution" => g with { NoAttributionRequired = false }, "budget" => g with { MaximumRequests = 101 },
            "interval" => g with { MinimumIntervalSeconds = 0 }, "timeout" => g with { TimeoutSeconds = 31 },
            "window" => g with { Canary = g.Canary with { To = g.Canary.To.AddDays(1) } },
            "category" => g with { Category = (AcquisitionCategory)999 }, "clock" => g with { ExpiresUtc = DateTime.SpecifyKind(g.ExpiresUtc, DateTimeKind.Unspecified) }, "payload" => g with { FixturePayloadApproved = false }, "provider" => g with { SourceCode = "other-provider" }, _ => g };
        Assert.Throws<ArgumentException>(g.Validate);
    }
    [Fact] public void Supported_explicit_grant_preserves_numeric_scope()
    { var g = Valid(); g.Validate(); Assert.Equal("39", g.Scope.CompetitionReference); Assert.Equal("2026", g.Scope.SeasonReference); }
}
