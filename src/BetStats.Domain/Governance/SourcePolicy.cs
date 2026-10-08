using BetStats.Domain.Common;

namespace BetStats.Domain.Governance;

public enum DataPurpose { MetadataDiscovery, DataRetrieval, RawPayloadStorage, HistoricalRetention, InternalAnalytics, PublicDisplay, ModelTraining, CommercialUse, Redistribution }
public enum PermissionDecision { Unknown, Allowed, Denied }
public enum PolicyStatus { Draft, Approved, Revoked }

public sealed class PurposePermission
{
    private PurposePermission() { }
    public PurposePermission(DataPurpose purpose, PermissionDecision decision, string? attribution = null, int? maximumRetentionDays = null)
    {
        Purpose = Require.Defined(purpose); Decision = Require.Defined(decision);
        Attribution = attribution is null ? null : Require.Text(attribution, 500);
        Require.That(maximumRetentionDays is null or > 0, "Retention limit must be positive.");
        MaximumRetentionDays = maximumRetentionDays;
    }
    public Guid SourcePolicyId { get; private set; }
    public DataPurpose Purpose { get; private set; }
    public PermissionDecision Decision { get; private set; }
    public string? Attribution { get; private set; }
    public int? MaximumRetentionDays { get; private set; }
}

public sealed class SourcePolicy
{
    private readonly List<PurposePermission> permissions = [];
    private readonly List<PolicyAudit> audit = [];
    private SourcePolicy() { }
    public SourcePolicy(Guid id, Guid dataSourceId, int version, DateTime effectiveFromUtc, DateTime? effectiveToUtc,
        string termsReference, string evidenceReference, DateTime createdAtUtc, IEnumerable<PurposePermission> permissions)
    {
        Id = Require.Id(id); DataSourceId = Require.Id(dataSourceId);
        Require.That(version > 0, "Policy version must be positive."); Version = version;
        EffectiveFromUtc = Require.Utc(effectiveFromUtc); EffectiveToUtc = Require.Utc(effectiveToUtc); CreatedAtUtc = Require.Utc(createdAtUtc);
        Require.That(effectiveToUtc is null || effectiveToUtc > effectiveFromUtc, "Effective interval must be nonempty.");
        TermsReference = Require.Text(termsReference, 1000); EvidenceReference = Require.Text(evidenceReference, 1000);
        ArgumentNullException.ThrowIfNull(permissions);
        var supplied = permissions.ToArray();
        Require.That(supplied.Select(p => p.Purpose).Distinct().Count() == supplied.Length, "Duplicate purpose permissions.");
        foreach (var purpose in Enum.GetValues<DataPurpose>())
        {
            var permission = supplied.SingleOrDefault(p => p.Purpose == purpose);
            // Own a copy: a permission cannot be shared between tracked policy aggregates.
            this.permissions.Add(permission is null ? new(purpose, PermissionDecision.Unknown)
                : new(purpose, permission.Decision, permission.Attribution, permission.MaximumRetentionDays));
        }
    }
    public Guid Id { get; private set; }
    public Guid DataSourceId { get; private set; }
    public int Version { get; private set; }
    public DateTime EffectiveFromUtc { get; private set; }
    public DateTime? EffectiveToUtc { get; private set; }
    public string TermsReference { get; private set; } = null!;
    public string EvidenceReference { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }
    public IReadOnlyCollection<PurposePermission> Permissions => permissions.AsReadOnly();
    public IReadOnlyCollection<PolicyAudit> Audit => audit.AsReadOnly();
    public PolicyStatus Status => audit.OrderByDescending(a => a.Sequence).FirstOrDefault()?.Status ?? PolicyStatus.Draft;
    public DateTime? ReviewedAtUtc => audit.OrderByDescending(a => a.Sequence).FirstOrDefault()?.ReviewedAtUtc;
    public DateTime? ApprovedAtUtc => audit.Where(a => a.Status == PolicyStatus.Approved).OrderByDescending(a => a.Sequence).FirstOrDefault()?.ApprovedAtUtc;
    public string? Reviewer => audit.OrderByDescending(a => a.Sequence).FirstOrDefault()?.Reviewer;

    public PolicyAudit Approve(Guid decisionId, string reviewer, string reason, DateTime reviewedAtUtc, DateTime approvedAtUtc)
    {
        Require.That(Status == PolicyStatus.Draft, "Only a draft can be approved; create a new version after revocation.");
        var decision = new PolicyAudit(decisionId, Id, PolicyStatus.Approved, reviewer, reason, reviewedAtUtc, approvedAtUtc, audit.LastOrDefault());
        audit.Add(decision); return decision;
    }
    public PolicyAudit Revoke(Guid decisionId, string reviewer, string reason, DateTime revokedAtUtc)
    {
        Require.That(Status == PolicyStatus.Approved, "Only an approved policy can be revoked.");
        var decision = new PolicyAudit(decisionId, Id, PolicyStatus.Revoked, reviewer, reason, revokedAtUtc, null, audit.OrderByDescending(a => a.Sequence).First());
        audit.Add(decision); return decision;
    }
}

public sealed class PolicyAudit
{
    private PolicyAudit() { }
    internal PolicyAudit(Guid id, Guid policyId, PolicyStatus status, string reviewer, string reason,
        DateTime reviewedAtUtc, DateTime? approvedAtUtc, PolicyAudit? previous)
    {
        Id = Require.Id(id); SourcePolicyId = Require.Id(policyId); Status = Require.Defined(status);
        Reviewer = Require.Text(reviewer, 200); Reason = Require.Text(reason, 500);
        ReviewedAtUtc = Require.Utc(reviewedAtUtc); ApprovedAtUtc = Require.Utc(approvedAtUtc);
        Require.That(approvedAtUtc is null || approvedAtUtc >= reviewedAtUtc, "Approval precedes review.");
        Sequence = previous is null ? 1 : checked(previous.Sequence + 1);
        PreviousAuditId = previous?.Id; PreviousSequence = previous?.Sequence;
    }
    public Guid Id { get; private set; }
    public Guid SourcePolicyId { get; private set; }
    public int Sequence { get; private set; }
    public PolicyStatus Status { get; private set; }
    public string Reviewer { get; private set; } = null!;
    public string Reason { get; private set; } = null!;
    public DateTime ReviewedAtUtc { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }
    public Guid? PreviousAuditId { get; private set; }
    public int? PreviousSequence { get; private set; }
}
