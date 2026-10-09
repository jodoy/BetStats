using BetStats.Application.Dashboard;

namespace BetStats.Api;

public static class DashboardEndpoints
{
    public static void MapDashboard(this WebApplication app)
    {
        var demo = app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Dashboard:DemoEnabled");
        var group = app.MapGroup("/api/v1/dashboard")
            .WithTags("Local dashboard").WithSummary("Read governed dashboard evidence")
            .WithDescription("Development loopback only. Bounded read-only projection with current display and retention checks; never executes models. DEMO requires explicit Development configuration.");
        group.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!DashboardAccess.Allowed(app.Environment.IsDevelopment(), context.HttpContext.Connection.RemoteIpAddress,
                request.Host.Host, request.Headers.Origin.ToString())) return Results.StatusCode(403);
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 403, title: "Current display permission denied"); }
            catch (Exception e) when (e is ArgumentException or FormatException) { return Results.Problem(statusCode: 400, title: "Invalid dashboard filter"); }
            catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or InvalidOperationException or IOException)
            { return Results.Problem(statusCode: 409, title: "Stored evidence unavailable"); }
        });
        IDashboardQueries Queries(IDashboardQueries queries) => demo ? new DemoDashboardQueries() : queries;
        group.MapGet("/fixtures", async ([AsParameters] DashboardFilter filter, IDashboardQueries queries, CancellationToken token) => await Queries(queries).FixturesAsync(filter, token));
        foreach (var kind in new[] { "competitions", "seasons", "teams" })
            group.MapGet("/" + kind, async ([AsParameters] DashboardFilter filter, IDashboardQueries queries, CancellationToken token) => await Queries(queries).ChoicesAsync(kind, filter, token));
        group.MapGet("/backtests", async ([AsParameters] DashboardFilter filter, IDashboardQueries queries, CancellationToken token) => await Queries(queries).BacktestsAsync(filter, token));
        group.MapGet("/models", async ([AsParameters] DashboardFilter filter, IDashboardQueries queries, CancellationToken token) => await Queries(queries).BacktestsAsync(filter, token));
        group.MapGet("/predictions", async ([AsParameters] DashboardFilter filter, IDashboardQueries queries, CancellationToken token) => await Queries(queries).BacktestsAsync(filter, token));
        group.MapGet("/quality", async ([AsParameters] DashboardFilter filter, IDashboardQueries queries, CancellationToken token) => await Queries(queries).QualityAsync(filter, token));
        group.MapGet("/provenance", async ([AsParameters] DashboardFilter filter, IDashboardQueries queries, CancellationToken token) => await Queries(queries).FixturesAsync(filter, token));
    }
}
