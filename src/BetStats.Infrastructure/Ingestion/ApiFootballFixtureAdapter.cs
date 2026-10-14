using System.Globalization;
using System.Net;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Sports;

namespace BetStats.Infrastructure.Ingestion;

// Constructed only behind the durable Development activation/reservation gate.
// Controlled tests can inject an invoker; no standalone adapter is registered.
public sealed class ApiFootballFixtureAdapter : IContentProviderAdapter
{
    public const string Profile = "api-football-fixtures-v1";
    private readonly HttpMessageInvoker http;
    private readonly string? apiKey;
    private readonly bool development;
    private readonly Func<bool> enabled;
    private readonly AcquisitionPageContext context;
    private readonly ApiFootballCanary canary;
    private readonly ISourcePolicyEvaluator policies;
    private readonly TimeProvider clock;
    public ProviderDescriptor Descriptor { get; }
    public RetrievedContent? Content { get; private set; }

    public ApiFootballFixtureAdapter(HttpMessageInvoker http, string? apiKey, bool development, Func<bool> enabled,
        AcquisitionPageContext context, ApiFootballCanary canary, ISourcePolicyEvaluator policies, TimeProvider clock)
    {
        this.http = http; this.apiKey = apiKey; this.development = development; this.enabled = enabled;
        this.context = context; this.canary = canary; this.policies = policies; this.clock = clock;
        Descriptor = new(context.SourceId, "api-football", [ReferenceSports.All.Single(s => s.Code == "football").Id],
            [ProviderCapability.HistoricalObservations]);
    }
    public override string ToString() => "API-Football disabled-by-default fixture boundary";
    public ConfigurationValidation ValidateConfiguration()
    {
        var valid = development && enabled() && !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length <= 256 &&
            apiKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') && canary.IsValid &&
            context.Version == 1 && context.PolicyId != Guid.Empty && context.PolicyVersion > 0 && context.Scope.IsValid &&
            context.Scope.CompetitionReference == canary.League.ToString(CultureInfo.InvariantCulture) &&
            context.Scope.SeasonReference == canary.Season.ToString(CultureInfo.InvariantCulture) &&
            context.Category is AcquisitionCategory.PrematchMetadata or AcquisitionCategory.PostmatchResults or AcquisitionCategory.HistoricalResults;
        return new(valid, valid ? [] : ["api_football_configuration_blocked"]);
    }
    public async Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        Content = null;
        if (!ValidateConfiguration().Valid) return Failure(ProviderErrorCategory.InvalidConfiguration);
        if (request.Capability != ProviderCapability.HistoricalObservations || !Descriptor.Sports.Contains(request.SportId))
            return Failure(ProviderErrorCategory.UnsupportedCapability);
        if (!await Allowed(request, cancellationToken)) return Failure(ProviderErrorCategory.PermissionDenied);
        // /fixtures has no documented page parameter. Never invent pagination parameters.
        var uri = FormattableString.Invariant($"https://v3.football.api-sports.io/fixtures?league={canary.League}&season={canary.Season}&from={canary.From:yyyy-MM-dd}&to={canary.To:yyyy-MM-dd}&timezone=UTC");
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Add("x-apisports-key", apiKey);
        message.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Failure(ProviderErrorCategory.AuthenticationFailure);
            if ((int)response.StatusCode == 429)
            {
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date is { } date ? date - clock.GetUtcNow() : TimeSpan.FromMinutes(1));
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                return new(false, new(ProviderErrorCategory.RateLimitExceeded, "api_football_rate_limited", delay));
            }
            if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 499)
                return Failure(ProviderErrorCategory.TemporaryUnavailability);
            // Redirects are failures. The registered pooled handler disables redirects/cookies/proxy.
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json" ||
                response.Content.Headers.ContentLength is > 1048576) return Failure(ProviderErrorCategory.InvalidResponse);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var bytes = new MemoryStream(); var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (bytes.Length + read > 1048576) return Failure(ProviderErrorCategory.InvalidResponse);
                await bytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (bytes.Length == 0) return Failure(ProviderErrorCategory.InvalidResponse);
            if (!ValidateConfiguration().Valid || !await Allowed(request, cancellationToken)) return Failure(ProviderErrorCategory.PermissionDenied);
            // Preserve exact JSON, including invalid JSON. Parsing must follow durable RAW capture.
            var now = clock.GetUtcNow().UtcDateTime; now = now.AddTicks(-now.Ticks % 10);
            Content = new(bytes.ToArray(), "application/json", now, Profile);
            return new(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        { return Failure(e is OperationCanceledException ? ProviderErrorCategory.Timeout : ProviderErrorCategory.TemporaryUnavailability); }
    }
    private async Task<bool> Allowed(ProviderRequest request, CancellationToken token)
    {
        foreach (var purpose in FootballProviderContract.ImportPurposes)
        {
            var now = clock.GetUtcNow().UtcDateTime; now = now.AddTicks(-now.Ticks % 10);
            var decision = await policies.EvaluateAsync(context.SourceId, purpose, now, request.Context, token);
            if (!decision.Allowed || decision.PolicyId != context.PolicyId || decision.Version != context.PolicyVersion) return false;
        }
        return true;
    }
    private static ProviderResult Failure(ProviderErrorCategory category) => new(false, new(category, "api_football_" + category));
}
