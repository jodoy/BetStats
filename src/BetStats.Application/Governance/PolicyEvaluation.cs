using BetStats.Domain.Governance;

namespace BetStats.Application.Governance;

public sealed record UsageContext(bool PublicDisplay = false, bool Commercial = false, bool ModelTraining = false,
    bool Redistribution = false, bool AttributionProvided = false, int? IntendedRetentionDays = null);
public sealed record PolicyState(SourcePolicy Policy, PolicyStatus Status);
public interface ISourcePolicyHistory
{
    Task<IReadOnlyList<PolicyState>> ReadAtAsync(Guid dataSourceId, DateTime atUtc, CancellationToken cancellationToken = default);
}
public enum PolicyReason { Authorized, MissingPolicy, Draft, Revoked, NotEffective, ConflictingPolicies, UnknownPermission, ExplicitDenial, RestrictionNotSatisfied }
public sealed record AppliedRestriction(DataPurpose Purpose, string? Attribution, int? MaximumRetentionDays);
public sealed record PolicyEvaluation(bool Allowed, PolicyReason Reason, Guid? PolicyId, int? Version,
    DateTime EvaluatedAtUtc, IReadOnlyList<AppliedRestriction> Restrictions);
public interface ISourcePolicyEvaluator
{
    Task<PolicyEvaluation> EvaluateAsync(Guid dataSourceId, DataPurpose purpose, DateTime atUtc, UsageContext context,
        CancellationToken cancellationToken = default);
}

public sealed class SourcePolicyEvaluator(ISourcePolicyHistory history) : ISourcePolicyEvaluator
{
    public async Task<PolicyEvaluation> EvaluateAsync(Guid dataSourceId, DataPurpose purpose, DateTime atUtc, UsageContext context, CancellationToken cancellationToken = default)
    {
        if (dataSourceId == Guid.Empty || !Enum.IsDefined(purpose) || atUtc.Kind != DateTimeKind.Utc || atUtc.Ticks % 10 != 0)
            throw new ArgumentException("Source, purpose and UTC evaluation time are required.");
        ArgumentNullException.ThrowIfNull(context);
        if (context.IntendedRetentionDays is <= 0) throw new ArgumentException("Retention must be positive.");
        var states = await history.ReadAtAsync(dataSourceId, atUtc, cancellationToken);
        var active = states.Where(s => s.Policy.DataSourceId == dataSourceId && s.Status == PolicyStatus.Approved &&
            s.Policy.EffectiveFromUtc <= atUtc && (s.Policy.EffectiveToUtc is null || atUtc < s.Policy.EffectiveToUtc)).ToArray();
        PolicyEvaluation Result(bool allowed, PolicyReason reason, SourcePolicy? policy = null, IReadOnlyList<AppliedRestriction>? restrictions = null) =>
            new(allowed, reason, policy?.Id, policy?.Version, atUtc, restrictions ?? []);
        if (active.Length > 1) return Result(false, PolicyReason.ConflictingPolicies);
        if (active.Length == 0)
        {
            var last = states.Where(s => s.Policy.DataSourceId == dataSourceId).OrderByDescending(s => s.Policy.Version).FirstOrDefault();
            return last is null ? Result(false, PolicyReason.MissingPolicy) : Result(false,
                last.Status switch { PolicyStatus.Draft => PolicyReason.Draft, PolicyStatus.Revoked => PolicyReason.Revoked, _ => PolicyReason.NotEffective }, last.Policy);
        }
        var policy = active[0].Policy;
        var purposes = new HashSet<DataPurpose> { purpose };
        if (context.PublicDisplay) purposes.Add(DataPurpose.PublicDisplay);
        if (context.Commercial) purposes.Add(DataPurpose.CommercialUse);
        if (context.ModelTraining) purposes.Add(DataPurpose.ModelTraining);
        if (context.Redistribution) purposes.Add(DataPurpose.Redistribution);
        var restrictions = new List<AppliedRestriction>();
        foreach (var requested in purposes.Order())
        {
            var permission = policy.Permissions.SingleOrDefault(p => p.Purpose == requested);
            if (permission is null || permission.Decision == PermissionDecision.Unknown) return Result(false, PolicyReason.UnknownPermission, policy, restrictions);
            if (permission.Decision == PermissionDecision.Denied) return Result(false, PolicyReason.ExplicitDenial, policy, restrictions);
            var restriction = new AppliedRestriction(requested, permission.Attribution, permission.MaximumRetentionDays);
            restrictions.Add(restriction);
            if ((permission.Attribution is not null && !context.AttributionProvided) ||
                (permission.MaximumRetentionDays is { } maximum && (context.IntendedRetentionDays is null || context.IntendedRetentionDays > maximum)))
                return Result(false, PolicyReason.RestrictionNotSatisfied, policy, restrictions);
        }
        return Result(true, PolicyReason.Authorized, policy, restrictions);
    }
}
