using System.Text.Json;
using BetStats.Application.Pipeline;
using BetStats.Application.Ingestion;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.Infrastructure.Pipeline;

public static class PipelineOperatorCommand
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }, WriteIndented = true };
    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration config, bool development, CancellationToken token = default)
    {
        if (!development) throw new InvalidOperationException("Pipeline commands require Development.");
        string Text(string key) => config["Pipeline:" + key] ?? throw new ArgumentException("Required Pipeline:" + key);
        Guid Id(string key) => Guid.Parse(Text(key));
        var actor = Text("Actor"); var reason = Text("Reason");
        var action = Text("Action"); var approval = new PipelineApproval(actor, reason, config["Pipeline:Approve"] == "true");
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Explicit audit claims required.");
        var jobs = services.GetRequiredService<PostgreSqlPipeline>();
        object? result = null; PipelineClaim? claim = null; var success = true;
        switch (action)
        {
            case "plan":
                byte[] bytes;
                await using (var file = new FileStream(Text("DefinitionPath"), FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous))
                {
                    if (file.Length is < 1 or > 1048576) throw new ArgumentException("Bounded definition file required.");
                    bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, token);
                    if (await file.ReadAsync(new byte[1], token) != 0) throw new ArgumentException("Definition file changed during read.");
                }
                result = await jobs.PlanAsync(Id("JobId"), JsonSerializer.Deserialize<PipelineDefinition>(bytes, JsonOptions) ?? throw new ArgumentException("Definition required."), approval, token); break;
            case "enable": case "disable": await jobs.SetEnabledAsync(Id("JobId"), action == "enable", approval, token); break;
            case "cancel": await jobs.CancelAsync(Id("ExecutionId"), approval, token); break;
            case "retry": case "recover": claim = await jobs.RetryAsync(Id("ExecutionId"), action == "recover", approval, token); break;
            case "run-once": claim = await jobs.AcquireAsync(approval, token); break;
            case "work":
                approval.Validate();
                var duration = int.Parse(config["Pipeline:DurationSeconds"] ?? "300", System.Globalization.CultureInfo.InvariantCulture);
                var maximum = int.Parse(config["Pipeline:MaximumExecutions"] ?? "10", System.Globalization.CultureInfo.InvariantCulture);
                if (duration is < 1 or > 3600 || maximum is < 1 or > 100) throw new ArgumentException("Bounded explicit worker duration and execution count required.");
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(duration)); var processed = 0;
                    try
                    {
                        while (processed < maximum)
                        {
                            var due = await jobs.AcquireAsync(approval, deadline.Token);
                            if (due is null) { await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token); continue; }
                            var executed = await RunClaim(services, jobs, due, approval, deadline.Token); processed++;
                            if (executed.State != PipelineState.Completed) success = false;
                        }
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                    result = new { ProcessedExecutions = processed, Infrastructure = "Available", ProviderDataReadiness = "NotCertified" };
                }
                break;
            case "inspect": result = await jobs.StatusAsync(Id("JobId"), token); break;
            case "status": result = await jobs.StatusAsync(null, token); break;
            default: throw new ArgumentException("Unsupported explicit pipeline action.");
        }
        if (claim is not null)
        {
            result = await RunClaim(services, jobs, claim, approval, token);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Version = 1, Action = action, Result = result }, JsonOptions));
        return !success || result is PipelineOutcome outcome && outcome.State != PipelineState.Completed ? 1 : 0;
    }

    private static async Task<PipelineOutcome> RunClaim(IServiceProvider services, PostgreSqlPipeline jobs, PipelineClaim claim, PipelineApproval approval, CancellationToken token)
    {
        await using var owner = await jobs.OwnAsync(claim.ExecutionId, token);
        Console.WriteLine(JsonSerializer.Serialize(new { Event = "pipeline_execution_started", CorrelationId = claim.ExecutionId, claim.JobId, claim.Attempt, claim.PlannedUtc, ActualExecutionUtc = claim.StartedUtc }));
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var heartbeatStop = new CancellationTokenSource();
        var connection = services.GetRequiredService<BetStatsDbContext>().Database.GetConnectionString();
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(100, claim.Definition.LeaseSeconds * 1000 / 3)));
                while (await timer.WaitForNextTickAsync(heartbeatStop.Token))
                {
                    await using var db = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(connection).Options);
                    await new PostgreSqlPipeline(db).RenewAsync(claim, heartbeatStop.Token);
                }
            }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
            catch { work.Cancel(); }
        }, CancellationToken.None);
        PipelineOutcome outcome; byte[]? artifact = null;
        try { (outcome, artifact) = await services.GetRequiredService<PipelineWork>().ExecuteAsync(claim, approval, work.Token); }
        catch (OperationCanceledException) { outcome = new(PipelineState.Cancelled, token.IsCancellationRequested ? "shutdown_requested" : "cancelled_or_lease_unavailable"); }
        catch (Exception error) when (InfrastructureFailure(error)) { outcome = new(PipelineState.Failed, "database_unavailable"); }
        catch (Exception error) when (error is UnauthorizedAccessException or IngestionDeniedException) { outcome = new(PipelineState.Blocked, "current_authorization_denied"); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException) { outcome = new(PipelineState.Blocked, "evidence_or_request_conflict"); }
        catch (IOException) { outcome = new(PipelineState.Failed, "local_input_unavailable"); }
        catch (Npgsql.NpgsqlException) { outcome = new(PipelineState.Failed, "database_unavailable"); }
        finally { heartbeatStop.Cancel(); await heartbeat; }
        // If the database is unavailable or the owner is lost, this fails and leaves a recoverable running receipt.
        // No successful completion is invented in memory.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            outcome = await jobs.CompleteAsync(claim, outcome, approval, completion.Token, artifact,
                artifact is null ? null : t => services.GetRequiredService<PipelineWork>().AuthorizePublicationAsync(claim, artifact, t));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IngestionDeniedException)
        {
            outcome = new(PipelineState.Blocked, "authorization_revoked_before_publication");
            outcome = await jobs.CompleteAsync(claim, outcome, approval, completion.Token);
        }
        catch (InvalidDataException)
        {
            outcome = new(PipelineState.Blocked, "publication_evidence_changed");
            outcome = await jobs.CompleteAsync(claim, outcome, approval, completion.Token);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Event = "pipeline_execution_finished", CorrelationId = claim.ExecutionId, claim.JobId, Outcome = outcome }));
        return outcome;
    }
    public static bool InfrastructureFailure(Exception error) => error is Npgsql.NpgsqlException || error.InnerException is { } inner && InfrastructureFailure(inner);
}
