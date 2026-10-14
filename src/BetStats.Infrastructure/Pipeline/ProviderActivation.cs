using System.Text.Json;
using BetStats.Application.Datasets;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Pipeline;
using BetStats.Application.Providers;
using BetStats.Domain.Governance;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Pipeline;

// Secret values are never part of serializable state. Rotation is read per request.
public sealed class ProviderTransportOptions(bool development, bool enabled, Func<string?> credential)
{
    public bool Development { get; } = development;
    public bool Enabled { get; } = enabled;
    internal string? Key() => credential();
    public bool CredentialPresent => !string.IsNullOrWhiteSpace(Key());
    public override string ToString() => "Provider transport options (secret omitted)";
}
public sealed class ApiFootballHttpTransport(HttpMessageInvoker http, bool controlled = false) : IDisposable
{
    internal bool Controlled { get; } = controlled;
    public void Dispose() => http.Dispose();
    internal ApiFootballFixtureAdapter Adapter(ProviderTransportOptions options, ProviderActivationGrant grant,
        ISourcePolicyEvaluator policies, TimeProvider clock) => new(http, options.Key(), options.Development,
            () => options.Enabled, new(1, grant.SourceId, grant.PolicyId, grant.PolicyVersion, grant.Scope, grant.Category), grant.Canary, policies, clock);
}

public sealed class ProviderActivation(BetStatsDbContext db, PostgreSqlPipeline jobs, ISourcePolicyEvaluator policies,
    IRawPayloadStore storage, FootballIngestionPersistence captures, ProviderPublication publications, FootballProspectiveReports prospective,
    AuthorizedProviderExecutor executor, ProviderTransportOptions options, ApiFootballHttpTransport transport,
    TimeProvider clock) : IProviderActivationReports
{
    private Task<DateTime> Now(CancellationToken t) => QualityPersistence.Now(db, t);
    private static ProviderActivationGrant Decode(ProviderGrantRecord record)
    {
        if (record.Hash != CanonicalDatasetJson.Hash(record.Content)) throw new InvalidDataException("Provider grant hash mismatch.");
        var g = CanonicalDatasetJson.Deserialize<ProviderActivationGrant>(record.Content); g.Validate();
        if (g.Id != record.Id || g.SourceId != record.SourceId || g.PolicyId != record.PolicyId) throw new InvalidDataException("Provider grant binding mismatch.");
        return g;
    }
    private async Task<ProviderActivationGrant> Grant(Guid id, CancellationToken t) => Decode(await db.Set<ProviderGrantRecord>().AsNoTracking().SingleAsync(x => x.Id == id, t));
    private async Task Rights(ProviderActivationGrant g, bool display, CancellationToken t)
    {
        g.Validate(); var now = await Now(t);
        if (g.ExpiresUtc <= now || !await db.DataSources.AnyAsync(s => s.Id == g.SourceId && s.Code == g.SourceCode && s.IsEnabled, t))
            throw new UnauthorizedAccessException("Provider grant expired or source unavailable.");
        if (g.EvidenceKind == BetStats.Application.Pipeline.ValidationEvidenceKind.OperatorDeclaredReal)
        {
            var policy = await db.SourcePolicies.AsNoTracking().SingleAsync(x => x.Id == g.PolicyId, t);
            if (policy.TermsReference.StartsWith("synthetic:", StringComparison.OrdinalIgnoreCase) || policy.EvidenceReference.StartsWith("synthetic:", StringComparison.OrdinalIgnoreCase) ||
                g.PermissionEvidence.StartsWith("synthetic:", StringComparison.OrdinalIgnoreCase) || g.EntitlementEvidence.StartsWith("synthetic:", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Synthetic approval cannot authorize real provider requests.");
        }
        foreach (var p in FootballProviderContract.ImportPurposes.Concat(display ? [DataPurpose.PublicDisplay] : Array.Empty<DataPurpose>()))
        {
            var decision = await policies.EvaluateAsync(g.SourceId, p, now, new(PublicDisplay: display, IntendedRetentionDays: 1), t);
            if (!decision.Allowed || decision.PolicyId != g.PolicyId || decision.Version != g.PolicyVersion || decision.Restrictions.Any(r => r.MaximumRetentionDays is not null))
                throw new UnauthorizedAccessException("Current provider rights denied.");
        }
    }
    private async Task Enabled(ProviderActivationGrant g, CancellationToken t)
    {
        if (!options.Development || !options.Enabled || !options.CredentialPresent) throw new UnauthorizedAccessException("Provider runtime disabled or credentials missing.");
        if (!transport.Controlled && g.EvidenceKind != BetStats.Application.Pipeline.ValidationEvidenceKind.OperatorDeclaredReal)
            throw new UnauthorizedAccessException("Real transport requires operator-declared real authorization.");
        if (!transport.Adapter(options, g, policies, clock).ValidateConfiguration().Valid) throw new UnauthorizedAccessException("Provider transport configuration invalid.");
        var e = await db.Set<ProviderActivationEvent>().AsNoTracking().Where(x => x.SourceId == g.SourceId).OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(t);
        if (e is null || !e.Enabled || e.GrantId != g.Id) throw new UnauthorizedAccessException("Explicit provider activation required.");
        await Rights(g, false, t);
    }
    public async Task<object> RegisterAsync(ProviderActivationGrant grant, PipelineApproval approval, CancellationToken t = default)
    {
        approval.Validate(); grant.Validate();
        if (!options.Development) throw new UnauthorizedAccessException("Development required.");
        db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(t);
        await QualityPersistence.Lock(db, grant.SourceId, t); await Rights(grant, false, t);
        var content = CanonicalDatasetJson.Serialize(grant); var hash = CanonicalDatasetJson.Hash(content);
        var old = await db.Set<ProviderGrantRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == grant.Id, t);
        if (old is not null && old.Hash != hash) throw new InvalidDataException("Immutable grant UUID conflict.");
        if (old is null)
        {
            db.Add(new ProviderGrantRecord { Id = grant.Id, SourceId = grant.SourceId, PolicyId = grant.PolicyId, Content = content, Hash = hash });
            db.Add(new ProviderActivationEvent { Id = Guid.NewGuid(), GrantId = grant.Id, SourceId = grant.SourceId, Enabled = false, Actor = approval.Actor, Reason = approval.Reason });
            await db.SaveChangesAsync(t);
        }
        await tx.CommitAsync(t); return new { grant.Id, Hash = hash, State = "RegisteredDisabled", Authorization = "OperatorDeclared" };
    }
    public async Task SetEnabledAsync(Guid grantId, bool enabled, PipelineApproval approval, CancellationToken t = default)
    {
        approval.Validate(); if (!options.Development) throw new UnauthorizedAccessException("Development required.");
        db.ChangeTracker.Clear(); var g = await Grant(grantId, t);
        await using var tx = await db.Database.BeginTransactionAsync(t); await QualityPersistence.Lock(db, g.SourceId, t);
        if (enabled)
        {
            if (!options.Enabled || !options.CredentialPresent) throw new UnauthorizedAccessException("Explicit runtime flag and credentials required.");
            if (!transport.Controlled && g.EvidenceKind != BetStats.Application.Pipeline.ValidationEvidenceKind.OperatorDeclaredReal)
                throw new UnauthorizedAccessException("Real transport requires operator-declared real authorization.");
            if (!transport.Adapter(options, g, policies, clock).ValidateConfiguration().Valid) throw new UnauthorizedAccessException("Provider transport configuration invalid.");
            await Rights(g, false, t);
        }
        db.Add(new ProviderActivationEvent { Id = Guid.NewGuid(), GrantId = grantId, SourceId = g.SourceId, Enabled = enabled, Actor = approval.Actor, Reason = approval.Reason });
        await db.SaveChangesAsync(t); await tx.CommitAsync(t);
    }
    public async Task<ProviderAcquisitionInput> PlanAsync(Guid grantId, CancellationToken t = default)
    {
        var g = await Grant(grantId, t); await Rights(g, false, t);
        return new(1, g.Id, CanonicalDatasetJson.Fingerprint(g));
    }
    private ProviderRequestEvent Event(ProviderRequestEvent r, PipelineClaim claim, string stage, string category,
        Guid? raw = null, Guid? publicationJob = null, DateTime? retry = null) => new() {
        Id = Guid.NewGuid(), RequestId = r.RequestId, SourceId = r.SourceId, GrantId = r.GrantId, ExecutionId = r.ExecutionId,
        Owner = claim.Owner, Attempt = claim.Attempt, RunId = r.RunId, Stage = stage, Category = category,
        RawId = raw, PublicationJobId = publicationJob, RetryUntilUtc = retry };
    private async Task Append(ProviderRequestEvent e, PipelineClaim claim, CancellationToken t)
    {
        db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(t);
        await LockClaim(claim, t); await QualityPersistence.Lock(db, e.SourceId, t); await jobs.CheckAsync(claim, t);
        if (!await db.Set<ProviderRequestEvent>().AnyAsync(x => x.ExecutionId == e.ExecutionId && x.Stage == e.Stage, t))
        {
            db.Add(e); await db.SaveChangesAsync(t);
            if (e.Stage is "Captured" or "Failed")
                await captures.CompleteAsync(new(e.RequestId, e.RunId, e.SourceId, e.Stage == "Captured" ? ImportOutcome.Succeeded : ImportOutcome.Failed,
                    RetrievedPayloads: e.Stage == "Captured" ? 1 : 0, ErrorCode: e.Stage == "Failed" ? "provider_request_failed" : null,
                    PolicyId: (await Grant(e.GrantId, t)).PolicyId), t);
        }
        await tx.CommitAsync(t);
    }
    private async Task LockClaim(PipelineClaim claim, CancellationToken t)
    {
        _ = await db.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM pipeline.\"Jobs\" WHERE \"Id\"={claim.JobId} FOR UPDATE").ToListAsync(t);
        _ = await db.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM pipeline.\"Executions\" WHERE \"Id\"={claim.ExecutionId} FOR UPDATE").ToListAsync(t);
    }
    public async Task<(PipelineOutcome Outcome, object? Result)> ExecuteAsync(PipelineClaim claim, ProviderAcquisitionInput input, PipelineApproval approval, CancellationToken t)
    {
        approval.Validate();
        if (claim.Definition.Kind != PipelineKind.ProviderAcquisition || input.Version != 1 ||
            CanonicalDatasetJson.Fingerprint(JsonSerializer.Deserialize<ProviderAcquisitionInput>(claim.Definition.PayloadJson, PipelineOperatorCommand.JsonOptions)) != CanonicalDatasetJson.Fingerprint(input))
            throw new ArgumentException("Pinned provider acquisition claim required.");
        var g = await Grant(input.GrantId, t);
        if (input.GrantHash != CanonicalDatasetJson.Fingerprint(g)) throw new InvalidDataException("Provider acquisition grant hash mismatch.");
        captures.ResetProviderAttempt();
        db.ChangeTracker.Clear(); ProviderRequestEvent reserved; var newReservation = false;
        await using (var tx = await db.Database.BeginTransactionAsync(t))
        {
            await LockClaim(claim, t); await QualityPersistence.Lock(db, g.SourceId, t);
            await jobs.CheckAsync(claim, t); await Enabled(g, t);
            var previous = await db.Set<ProviderRequestEvent>().AsNoTracking().Where(x => x.ExecutionId == claim.ExecutionId).OrderBy(x => x.Sequence).ToListAsync(t);
            if (previous.Count > 0) reserved = previous.Single(x => x.Stage == "Reserved");
            else
            {
                var events = await db.Set<ProviderRequestEvent>().AsNoTracking().Where(x => x.SourceId == g.SourceId).ToListAsync(t);
                var reservations = events.Where(x => x.Stage == "Reserved").ToArray(); var now = await Now(t);
                if (reservations.Length >= g.MaximumRequests) return (new(PipelineState.Blocked, "provider_budget_exhausted"), null);
                if (reservations.Any(r => !events.Any(e => e.RequestId == r.RequestId && e.Stage is "Failed" or "Captured" or "Reconciled")))
                    return (new(PipelineState.Blocked, "provider_request_in_flight_or_uncertain"), null);
                if (reservations.Any(r => r.RecordedUtc.AddSeconds(g.MinimumIntervalSeconds) > now) || events.Any(e => e.RetryUntilUtc > now))
                    return (new(PipelineState.Blocked, "provider_rate_limit_wait"), null);
                var run = Guid.NewGuid();
                db.Add(new IngestionRun { Id = run, DataSourceId = g.SourceId, CreatedAtUtc = now, StartedAtUtc = now, Status = IngestionRunStatus.Running });
                reserved = new() { Id = Guid.NewGuid(), RequestId = Guid.NewGuid(), SourceId = g.SourceId, GrantId = g.Id,
                    ExecutionId = claim.ExecutionId, Owner = claim.Owner, Attempt = claim.Attempt, RunId = run, Stage = "Reserved", Category = "request_reserved", RetryUntilUtc = now.AddSeconds(g.MinimumIntervalSeconds) };
                db.Add(reserved);
                db.Add(new IngestionAuditEvent { Id = Guid.NewGuid(), AttemptId = reserved.RequestId, Sequence = 1,
                    RunId = run, DataSourceId = g.SourceId, Outcome = ImportOutcome.Running, AtUtc = now, PolicyId = g.PolicyId });
                await db.SaveChangesAsync(t); newReservation = true;
            }
            await jobs.CheckAsync(claim, t); await tx.CommitAsync(t);
        }
        var raw = await db.RawPayloads.AsNoTracking().SingleOrDefaultAsync(x => x.IngestionRunId == reserved.RunId, t);
        var history = await db.Set<ProviderRequestEvent>().AsNoTracking().Where(x => x.ExecutionId == claim.ExecutionId).ToListAsync(t);
        if (raw is null)
        {
            if (history.Any(x => x.Stage == "Failed"))
                return (new(PipelineState.Blocked, "provider_previous_request_failed_new_dispatch_required"), null);
            // A reservation from an earlier invocation is never an instruction to re-download.
            if (reserved.Owner != claim.Owner || reserved.Attempt != claim.Attempt || history.Any(x => x.Stage != "Reserved") ||
                reserved.RecordedUtc < claim.StartedUtc || !newReservation)
                return (new(PipelineState.Blocked, "provider_response_uncertain_requires_reconciliation"), null);
            await jobs.CheckAsync(claim, t); await Enabled(g, t);
            var adapter = transport.Adapter(options, g, policies, clock);
            var result = await executor.ExecuteAsync(adapter,
                new(ReferenceSports.All.Single(x => x.Code == "football").Id, ProviderCapability.HistoricalObservations, new(IntendedRetentionDays: 1)),
                new RequestBudget(new(1, 1, 1, TimeSpan.FromSeconds(g.TimeoutSeconds)), clock), t);
            if (!result.Success)
            {
                var retry = result.Error?.RetryAfter is { } delay ? (await Now(t)).Add(delay) : (DateTime?)null;
                await Append(Event(reserved, claim, "Failed", result.Error!.Category.ToString(), retry: retry), claim, t);
                return (new(PipelineState.Blocked, "provider_" + result.Error.Category), null);
            }
            await jobs.CheckAsync(claim, t); await Enabled(g, t);
            var content = adapter.Content ?? throw new InvalidDataException("Provider content absent.");
            var capture = await captures.CaptureProviderAsync(new(reserved.RequestId, reserved.RunId, g.SourceId, ImportOutcome.Running), content, g.Scope,
                claim, async ct => { await jobs.CheckAsync(claim, ct); await Enabled(g, ct); }, t);
            await storage.FinalizeAsync(capture.Object, t);
            raw = await db.RawPayloads.AsNoTracking().SingleAsync(x => x.Id == capture.Id, t);
        }
        // Finalize is idempotent; staged bytes remain readable after a capture-before-finalize crash.
        await storage.FinalizeAsync(new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength!.Value), t);
        await Append(Event(reserved, claim, "Captured", "original_raw_captured", raw.Id), claim, t);
        ProviderPublicationInput publication;
        try { publication = await publications.PlanAsync(raw.Id, g.Canary, g.EvidenceKind, t); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or JsonException)
        { return (new(PipelineState.Blocked, "provider_original_raw_invalid"), new { RawId = raw.Id }); }
        var publicationJob = PipelineOperationIds.Child(claim.ExecutionId, "provider-publication");
        var publicationDefinition = new PipelineDefinition(1, PipelineKind.ProviderPublication, new(raw.RecordedAtUtc), JsonSerializer.Serialize(publication));
        var prepared = await db.Set<ProviderRequestEvent>().AsNoTracking().AnyAsync(x => x.ExecutionId == claim.ExecutionId && x.Stage == "Prepared", t);
        if (!prepared)
        {
            await jobs.PlanAsync(publicationJob, publicationDefinition, approval, t, createOnly: true);
            await Append(Event(reserved, claim, "Prepared", "publication_job_disabled", raw.Id, publicationJob), claim, t);
        }
        var currentPublication = await db.Set<PipelineJobVersion>().AsNoTracking().Where(x => x.JobId == publicationJob).OrderByDescending(x => x.Version).FirstAsync(t);
        if (currentPublication.Hash != CanonicalDatasetJson.Fingerprint(publicationDefinition)) throw new InvalidDataException("Prepared publication job changed; original RAW retained.");
        await Enabled(g, t); await jobs.CheckAsync(claim, t);
        return (new(PipelineState.Completed, "provider_raw_captured_publication_requires_dispatch"),
            new { Version = 1, Kind = "provider-acquisition-v1", GrantId = g.Id, RawId = raw.Id, RawHash = raw.ContentHashSha256, raw.RecordedAtUtc, PublicationJobId = publicationJob, Origin = g.EvidenceKind });
    }
    public async Task ReconcileAsync(Guid executionId, PipelineApproval approval, CancellationToken t = default)
    {
        approval.Validate(); if (!options.Development) throw new UnauthorizedAccessException("Development required.");
        db.ChangeTracker.Clear(); var r = await db.Set<ProviderRequestEvent>().AsNoTracking().SingleAsync(x => x.ExecutionId == executionId && x.Stage == "Reserved", t);
        await using var tx = await db.Database.BeginTransactionAsync(t);
        _ = await db.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM pipeline.\"Jobs\" WHERE \"Id\"=(SELECT \"JobId\" FROM pipeline.\"Executions\" WHERE \"Id\"={executionId}) FOR UPDATE").ToListAsync(t);
        var execution = (await db.Set<PipelineExecution>().FromSqlInterpolated($"SELECT * FROM pipeline.\"Executions\" WHERE \"Id\"={executionId} FOR UPDATE").ToListAsync(t)).Single();
        await QualityPersistence.Lock(db, r.SourceId, t);
        if (execution.State == PipelineState.Running || await db.RawPayloads.AnyAsync(x => x.IngestionRunId == r.RunId, t))
            throw new InvalidOperationException("Finish or recover execution; original RAW cannot be abandoned.");
        if (!await db.Set<ProviderRequestEvent>().AnyAsync(x => x.ExecutionId == executionId && x.Stage == "Reconciled", t))
        {
            db.Add(new ProviderRequestEvent { Id = Guid.NewGuid(), RequestId = r.RequestId, SourceId = r.SourceId, GrantId = r.GrantId,
                ExecutionId = r.ExecutionId, Owner = r.Owner, Attempt = r.Attempt, RunId = r.RunId, Stage = "Reconciled", Category = "operator_acknowledged_uncertain_no_refund" });
            // Actor/reason remain in the existing immutable pipeline audit.
            db.Add(new PipelineReceipt { Id = Guid.NewGuid(), JobId = execution.JobId, ExecutionId = executionId, Owner = execution.Owner,
                State = execution.State, Category = "provider_uncertainty_reconciled", Actor = approval.Actor, Reason = approval.Reason, RecordedUtc = await Now(t) });
            await db.SaveChangesAsync(t);
            await captures.CompleteAsync(new(r.RequestId, r.RunId, r.SourceId, ImportOutcome.Failed,
                ErrorCode: "operator_acknowledged_uncertain_response", PolicyId: (await Grant(r.GrantId, t)).PolicyId), t);
        }
        await tx.CommitAsync(t);
    }
    public async Task AuthorizeAcknowledgmentAsync(PipelineClaim claim, CancellationToken t)
    {
        var input = JsonSerializer.Deserialize<ProviderAcquisitionInput>(claim.Definition.PayloadJson, PipelineOperatorCommand.JsonOptions)!;
        var g = await Grant(input.GrantId, t); await QualityPersistence.Lock(db, g.SourceId, t); await Enabled(g, t);
        var r = await db.Set<ProviderRequestEvent>().AsNoTracking().SingleAsync(x => x.ExecutionId == claim.ExecutionId && x.Stage == "Prepared", t);
        _ = await publications.PlanAsync(r.RawId!.Value, g.Canary, g.EvidenceKind, t);
    }
    public async Task<ProviderActivationStatus> StatusAsync(Guid sourceId, bool display, CancellationToken t = default)
    {
        var e = await db.Set<ProviderActivationEvent>().AsNoTracking().Where(x => x.SourceId == sourceId).OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(t);
        ProviderActivationGrant? g = e is null ? null : await Grant(e.GrantId, t); var blockers = new List<string>(); var authorized = false;
        if (g is null) blockers.Add("operator_authorization_missing");
        else
        {
            if (display) await Rights(g, true, t);
            try { await Rights(g, false, t); authorized = true; } catch (UnauthorizedAccessException) { blockers.Add("current_source_rights_or_entitlement_denied"); }
        }
        if (!options.Development) blockers.Add("development_required");
        if (!options.Enabled) blockers.Add("runtime_enable_flag_missing");
        if (!options.CredentialPresent) blockers.Add("credentials_missing");
        if (g is not null && !transport.Controlled && g.EvidenceKind != BetStats.Application.Pipeline.ValidationEvidenceKind.OperatorDeclaredReal)
            blockers.Add("synthetic_authorization_cannot_enable_real_transport");
        if (e?.Enabled != true) blockers.Add("explicit_activation_missing");
        var used = await db.Set<ProviderRequestEvent>().CountAsync(x => x.SourceId == sourceId && x.Stage == "Reserved", t);
        var attempts = await db.Set<ProviderRequestEvent>().AsNoTracking().Where(x => x.SourceId == sourceId).OrderByDescending(x => x.Sequence).Take(100).ToListAsync(t);
        var lastRaw = attempts.FirstOrDefault(x => x.RawId is not null); var lastPrepared = attempts.FirstOrDefault(x => x.PublicationJobId is not null);
        DateTime? next = attempts.Where(x => x.RetryUntilUtc is not null).Select(x => x.RetryUntilUtc).Max();
        var lastReservation = attempts.FirstOrDefault(x => x.Stage == "Reserved");
        if (lastReservation is not null && g is not null && (next is null || lastReservation.RecordedUtc.AddSeconds(g.MinimumIntervalSeconds) > next)) next = lastReservation.RecordedUtc.AddSeconds(g.MinimumIntervalSeconds);
        if (g is not null && used >= g.MaximumRequests) blockers.Add("provider_budget_exhausted");
        if (lastReservation is not null && !attempts.Any(x => x.RequestId == lastReservation.RequestId && x.Stage is "Failed" or "Captured" or "Reconciled")) blockers.Add("provider_request_in_flight_or_uncertain");
        if (next > await Now(t)) blockers.Add("provider_rate_limit_wait");
        var publication = authorized && lastPrepared?.PublicationJobId is { } publicationId ? await publications.StatusAsync(publicationId, display, t) : null;
        var operational = authorized && e?.Enabled == true && options.Development && options.Enabled && options.CredentialPresent &&
            lastRaw is not null && g?.EvidenceKind == BetStats.Application.Pipeline.ValidationEvidenceKind.OperatorDeclaredReal &&
            publication?.Receipts.Any(r => r.Report.State == ProviderPublicationState.Published && r.Verified) == true;
        ProspectiveCohortArtifact? cohort = null;
        if (authorized && lastRaw is not null)
        {
            var cohortIds = await db.Database.SqlQuery<Guid>($"""
                SELECT a."ExecutionId" AS "Value" FROM pipeline."Artifacts" a
                WHERE convert_from(a."Content",'UTF8')::jsonb->'Result'->>'Kind'='prospective-cohort-v1'
                  AND convert_from(a."Content",'UTF8')::jsonb->'Result'->'Report'->>'SourceId'={sourceId.ToString()}
                ORDER BY a."RecordedUtc" DESC LIMIT 1
                """).ToListAsync(t);
            if (cohortIds.Count > 0)
            {
                var stored = await prospective.CohortAsync(cohortIds[0], display, t);
                if (stored.Report.Acquisitions.Any(a => a.Report.EvidenceRaws.Any(r => r.Id == lastRaw.RawId))) cohort = stored;
            }
        }
        return new(sourceId, g?.Id, g is not null, authorized && (transport.Controlled || g?.EvidenceKind == BetStats.Application.Pipeline.ValidationEvidenceKind.OperatorDeclaredReal), e?.Enabled == true, options.CredentialPresent, operational,
            g?.EvidenceKind == BetStats.Application.Pipeline.ValidationEvidenceKind.Synthetic ? "SyntheticTestOnly" : "OperatorDeclared", g?.EvidenceKind, g?.Canary, g?.Category, g?.PolicyId, g?.PolicyVersion, g?.MaximumRequests, used, g is null ? null : Math.Max(0, g.MaximumRequests - used), null, next, blockers,
            attempts.Select(x => new ProviderRequestEvidence(x.RequestId, x.ExecutionId, x.Attempt, x.Stage, x.Category, x.RecordedUtc, x.RawId, x.PublicationJobId, x.RetryUntilUtc)).ToArray(),
            lastRaw?.RawId, lastPrepared?.PublicationJobId, cohort is null ? "Unavailable_ExplicitCohortRequired" : cohort.Report.EligiblePredictions > 0 ? "EligibleFrozenEvidenceObserved" : "BlockedByExistingEvidenceGates",
            cohort is null ? "Unavailable_ExplicitCohortRequired" : cohort.Report.State.ToString(), publication, cohort);
    }
}
