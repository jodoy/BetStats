using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Governance;
using BetStats.Domain.Sports;

namespace BetStats.Infrastructure.Ingestion;

// No HTTP implementation or DI registration exists. A reviewed provider implementation
// must supply transport; tests use controlled transport. Every page uses the existing executor.
public sealed class GovernedFootballPageAdapter(AcquisitionCapability capability, FootballImportScope scope,
    AcquisitionCategory category, IFootballApiTransport transport, FootballApiPageRequest page,
    ISourcePolicyEvaluator policies, TimeProvider clock) : IContentProviderAdapter
{
    public ProviderDescriptor Descriptor { get; } = new(capability.SourceId, capability.Provider,
        [ReferenceSports.All.Single(s => s.Code == "football").Id], [ProviderCapability.HistoricalObservations]);
    public RetrievedContent? Content { get; private set; }
    public string? NextCursor { get; private set; }
    public ConfigurationValidation ValidateConfiguration()
    {
        try { capability.Validate(); }
        catch (ArgumentException) { return new(false, ["invalid_acquisition_capability"]); }
        var valid = capability.Transport == FootballTransport.Api && scope.IsValid && capability.Categories.Contains(category) &&
            category != AcquisitionCategory.KickoffEvidence && page.PageSize is >= 1 and <= 500 && page.Cursor is not { Length: > 500 };
        return new(valid, valid ? [] : ["invalid_acquisition_configuration"]);
    }
    public async Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        Content = null; NextCursor = null;
        if (!ValidateConfiguration().Valid || request.Capability != ProviderCapability.HistoricalObservations)
            return new(false, new(ProviderErrorCategory.InvalidConfiguration, "invalid_acquisition_configuration"));
        var at = clock.GetUtcNow().UtcDateTime; at = at.AddTicks(-at.Ticks % 10);
        foreach (var purpose in FootballProviderContract.ImportPurposes)
        {
            var decision = await policies.EvaluateAsync(capability.SourceId, purpose, at, request.Context, cancellationToken);
            if (!decision.Allowed || decision.PolicyId != capability.PolicyId || decision.Version != capability.PolicyVersion)
                return new(false, new(ProviderErrorCategory.PermissionDenied, "acquisition_policy_changed"));
        }
        var response = await transport.ReadPageAsync(page with { Acquisition = new(1, capability.SourceId, capability.PolicyId, capability.PolicyVersion, scope, category) }, cancellationToken);
        if (response.Error is { } error)
            return new(false, new(error.Category, "controlled_provider_failure", error.RetryAfter));
        if (response.Bytes.Length < 1 || response.Bytes.Length > capability.MaximumBytes || response.NextCursor is { Length: > 500 })
            return new(false, new(ProviderErrorCategory.InvalidResponse, "bounded_response_required"));
        foreach (var purpose in FootballProviderContract.ImportPurposes)
        {
            var current = clock.GetUtcNow().UtcDateTime; current = current.AddTicks(-current.Ticks % 10);
            var decision = await policies.EvaluateAsync(capability.SourceId, purpose, current, request.Context, cancellationToken);
            if (!decision.Allowed || decision.PolicyId != capability.PolicyId || decision.Version != capability.PolicyVersion)
                return new(false, new(ProviderErrorCategory.PermissionDenied, "acquisition_policy_changed"));
        }
        var now = clock.GetUtcNow().UtcDateTime; now = now.AddTicks(-now.Ticks % 10);
        Content = new(response.Bytes, "text/csv", now, capability.Profile); NextCursor = response.NextCursor;
        return new(true);
    }
}

public sealed record ControlledAcquisitionPages(IReadOnlyList<RetrievedContent> Pages, ProviderResult Result);
public static class GovernedFootballPages
{
    public static async Task<ControlledAcquisitionPages> ReadAsync(AcquisitionCapability capability, FootballImportScope scope, AcquisitionCategory category,
        IFootballApiTransport transport, ISourcePolicyEvaluator policies, TimeProvider clock, AuthorizedProviderExecutor executor,
        RequestBudget budget, CancellationToken token = default)
    {
        capability.Validate(); var pagination = new FootballPagination(capability.MaximumPages, 500);
        var pages = new List<RetrievedContent>(); string? cursor = null; var total = 0;
        do
        {
            FootballApiPageRequest request;
            try { request = pagination.Next(cursor); }
            catch (InvalidOperationException) { return new(pages, new(false, new(ProviderErrorCategory.InvalidResponse, "pagination_bound_or_cycle"))); }
            var adapter = new GovernedFootballPageAdapter(capability, scope, category, transport, request, policies, clock);
            var result = await executor.ExecuteAsync(adapter, new(ReferenceSports.All.Single(s => s.Code == "football").Id, ProviderCapability.HistoricalObservations, new()), budget, token);
            if (!result.Success) return new(pages, result);
            total += adapter.Content!.Bytes.Length;
            if (total > capability.MaximumBytes) return new(pages, new(false, new(ProviderErrorCategory.InvalidResponse, "total_response_bound")));
            pages.Add(adapter.Content); cursor = adapter.NextCursor;
        } while (cursor is not null);
        return new(pages, new(true));
    }
}
