using BetStats.Application.Ingestion;
using BetStats.Application.Pipeline;

namespace BetStats.Application.Providers;

// Evidence references are operator attestations, never provider-independent certification.
public sealed record ProviderActivationGrant(int Version, Guid Id, Guid SourceId, string SourceCode,
    Guid PolicyId, int PolicyVersion, ApiFootballCanary Canary, AcquisitionCategory Category,
    string PermissionEvidence, string EntitlementEvidence, string Reviewer, DateTime ExpiresUtc,
    int MaximumRequests, int MinimumIntervalSeconds, int TimeoutSeconds,
    bool DurableRetentionApproved, bool NoAttributionRequired, ValidationEvidenceKind EvidenceKind, bool FixturePayloadApproved = false)
{
    public void Validate()
    {
        if (Version != 1 || Id == Guid.Empty || SourceId == Guid.Empty || PolicyId == Guid.Empty || PolicyVersion < 1 ||
            string.IsNullOrWhiteSpace(SourceCode) || SourceCode.Length > 100 || Canary is null || !Canary.IsValid ||
            (SourceCode != "api-football" && !SourceCode.StartsWith("api-football-", StringComparison.Ordinal)) ||
            Category is not (AcquisitionCategory.PrematchMetadata or AcquisitionCategory.PostmatchResults or AcquisitionCategory.HistoricalResults) ||
            new[] { PermissionEvidence, EntitlementEvidence, Reviewer }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 500) ||
            ExpiresUtc.Kind != DateTimeKind.Utc || ExpiresUtc.Ticks % 10 != 0 || MaximumRequests is < 1 or > 100 ||
            MinimumIntervalSeconds is < 1 or > 86400 || TimeoutSeconds is < 1 or > 30 ||
            !DurableRetentionApproved || !NoAttributionRequired || !FixturePayloadApproved || !Enum.IsDefined(EvidenceKind))
            throw new ArgumentException("Explicit supported provider grant, scope, obligations and bounded budget required.");
    }
    public FootballImportScope Scope => new(Canary.League.ToString(System.Globalization.CultureInfo.InvariantCulture), Canary.Season.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public IReadOnlyList<string> ApprovedDataCategories => ["complete_fixtures_response_raw", "fixture_metadata", "provider_identities", "kickoff_status", "regulation_results"];
    public string Provider => "api-football";
}
public sealed record ProviderAcquisitionInput(int Version, Guid GrantId, string GrantHash);
public sealed record ProviderRequestEvidence(Guid Id, Guid ExecutionId, int Attempt, string Stage, string Category,
    DateTime RecordedUtc, Guid? RawId, Guid? PublicationJobId, DateTime? RetryUntilUtc);
public sealed record ProviderActivationStatus(Guid SourceId, Guid? GrantId, bool Configured, bool Authorized,
    bool Enabled, bool CredentialPresent, bool Operational, string AuthorizationKind, ValidationEvidenceKind? EvidenceKind,
    ApiFootballCanary? Canary, AcquisitionCategory? Category, Guid? PolicyId, int? PolicyVersion, int? MaximumRequests, int UsedRequests, int? RemainingRequests,
    int? ProviderRemainingQuota, DateTime? NextRequestUtc, IReadOnlyList<string> Blockers,
    IReadOnlyList<ProviderRequestEvidence> Attempts, Guid? LatestRawId, Guid? PublicationJobId,
    string PredictionReadiness, string ProspectiveCohortAvailability, ProviderPublicationStatus? Publication,
    ProspectiveCohortArtifact? Cohort);
public interface IProviderActivationReports
{
    Task<ProviderActivationStatus> StatusAsync(Guid sourceId, bool display, CancellationToken token = default);
}
