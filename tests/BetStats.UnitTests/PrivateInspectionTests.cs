using BetStats.Application.Governance;
using BetStats.Domain.Governance;

namespace BetStats.UnitTests;

[Trait("Category", "Security")]
public sealed class PrivateInspectionTests
{
    private sealed class History(SourcePolicy policy) : ISourcePolicyHistory
    {
        public Task<IReadOnlyList<PolicyState>> ReadAtAsync(Guid source, DateTime at, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<PolicyState>>([new(policy, policy.Status)]);
    }
    [Fact]
    public async Task Private_analysis_cannot_authorize_public_display_and_revocation_is_immediate()
    {
        var now = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc); var source = Guid.NewGuid();
        var policy = new SourcePolicy(Guid.NewGuid(), source, 1, now, null, "synthetic:private", "synthetic:permission", now,
            [new(DataPurpose.InternalAnalytics, PermissionDecision.Allowed), new(DataPurpose.PublicDisplay, PermissionDecision.Denied)]);
        policy.Approve(Guid.NewGuid(), "test:operator", "Private only", now, now);
        var evaluator = new SourcePolicyEvaluator(new History(policy)); var audience = InspectionAudience.PrivateOperatorInspection;
        Assert.True((await evaluator.EvaluateAsync(source, audience.Purpose, now, audience.Context)).Allowed);
        Assert.False((await evaluator.EvaluateAsync(source, DataPurpose.PublicDisplay, now, audience.Context)).Allowed);
        policy.Revoke(Guid.NewGuid(), "test:operator", "Revoked", now);
        Assert.False((await evaluator.EvaluateAsync(source, audience.Purpose, now, audience.Context)).Allowed);
    }
    [Fact]
    public void Request_context_defaults_public_and_returns_to_public_after_end()
    {
        var context = new OperatorInspectionContext(); Assert.True(context.Audience.IsPublic);
        context.Begin(); Assert.True(context.Audience.IsPrivate); Assert.False(context.Audience.Context.PublicDisplay);
        context.End(); Assert.True(context.Audience.IsPublic);
        Assert.True(((InspectionAudience)true).Context.PublicDisplay);
        Assert.False((bool)(InspectionAudience)false);
    }
}
