using BetStats.Application.Football;
using Microsoft.AspNetCore.Http.HttpResults;

public static class DevelopmentFootballEndpoints
{
    public static void MapDevelopmentFootball(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        var group = app.MapGroup("/api/v1/dev/events").WithTags("Fictional football (Development)");
        group.MapGet("", async Task<Results<Ok<DevelopmentFootballPage>, ProblemHttpResult>> (DateTime asOfUtc, int? offset, int? limit, IServiceProvider services, CancellationToken token) =>
        {
            try { return TypedResults.Ok(await Service(services).ReadAsync(asOfUtc, offset ?? 0, limit ?? 20, token: token)); }
            catch (ArgumentException) { return BadQuery(); }
            catch (UnauthorizedAccessException) { return Forbidden(); }
            catch (InvalidOperationException) { return Unavailable(); }
        }).WithSummary("List project-owned fictional events as known at a cutoff")
            .WithDescription("Development only. Required asOfUtc is UTC with microsecond precision. Offset defaults to 0 (maximum 10000); limit defaults to 20 (1..100). Stable event UUID ordering. Requires explicit synthetic demo and permitted PostgreSQL/RAW evidence; no private provider data.")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(503).ProducesProblem(500);
        group.MapGet("/{id:guid}", async Task<Results<Ok<DevelopmentFootballEvent>, ProblemHttpResult>> (Guid id, DateTime asOfUtc, IServiceProvider services, CancellationToken token) =>
        {
            try
            {
                var page = await Service(services).ReadAsync(asOfUtc, 0, 1, id, token: token);
                return page.Items.Count == 0 ? Missing() : TypedResults.Ok(page.Items[0]);
            }
            catch (ArgumentException) { return BadQuery(); }
            catch (UnauthorizedAccessException) { return Forbidden(); }
            catch (InvalidOperationException) { return Unavailable(); }
        }).WithSummary("Read a fictional event at an explicit historical cutoff")
            .WithDescription("Development only. Required UTC asOfUtc; absent or non-fixture evidence returns 404; policy/retention denial returns 403. Returns sanitized scores and status, not provider/audit identifiers.")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503).ProducesProblem(500);
        group.MapGet("/{id:guid}/result", async Task<Results<Ok<DevelopmentFootballResult>, ProblemHttpResult>> (Guid id, DateTime asOfUtc, IServiceProvider services, CancellationToken token) =>
        {
            try
            {
                var page = await Service(services).ReadAsync(asOfUtc, 0, 1, id, token: token);
                return page.Items.Count == 0 ? Missing() : TypedResults.Ok(page.Items[0].Result);
            }
            catch (ArgumentException) { return BadQuery(); }
            catch (UnauthorizedAccessException) { return Forbidden(); }
            catch (InvalidOperationException) { return Unavailable(); }
        }).WithSummary("Read fictional scores and justified outcome labels")
            .WithDescription("Development only. Required UTC asOfUtc. Full-time labels require Finished regulation-time scores; confirmed HalfTime can supply only HalfTimeTotalGoals. Unknown full-time/half-time values stay null. Later corrections are excluded before their availability and database recording cutoff.")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503).ProducesProblem(500);
        group.MapGet("/{id:guid}/history", async Task<Results<Ok<DevelopmentFootballPage>, ProblemHttpResult>> (Guid id, DateTime asOfUtc, int? offset, int? limit, IServiceProvider services, CancellationToken token) =>
        {
            try { return TypedResults.Ok(await Service(services).ReadAsync(asOfUtc, offset ?? 0, limit ?? 20, id, history: true, token)); }
            catch (ArgumentException) { return BadQuery(); }
            catch (UnauthorizedAccessException) { return Forbidden(); }
            catch (InvalidOperationException) { return Unavailable(); }
        }).WithSummary("Read bounded fictional result corrections known at a cutoff")
            .WithDescription("Development only. Required UTC asOfUtc; offset 0..10000, limit 1..100 (defaults 0,20). Ordered by event UUID and availability. Empty page for absent/ineligible evidence. Immutable historical values remain visible; internal audit metadata is omitted.")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(503).ProducesProblem(500);
    }
    private static IDevelopmentFootball Service(IServiceProvider services) => services.GetRequiredService<IDevelopmentFootball>();
    private static ProblemHttpResult BadQuery() => TypedResults.Problem(statusCode: 400, title: "Invalid historical query", detail: "Supply a UTC microsecond cutoff and bounded pagination.");
    private static ProblemHttpResult Forbidden() => TypedResults.Problem(statusCode: 403, title: "Fictional evidence usage not authorized");
    private static ProblemHttpResult Missing() => TypedResults.Problem(statusCode: 404, title: "Fictional event unavailable at cutoff");
    private static ProblemHttpResult Unavailable() => TypedResults.Problem(statusCode: 503, title: "Development evidence unavailable", detail: "Configure PostgreSQL and run the explicit synthetic results demo.");
}
