using BetStats.Infrastructure;
using BetStats.Application.Sports;
using Microsoft.AspNetCore.Http.HttpResults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict);
builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "BetStats API";
    document.Info.Version = "v1";
    document.Info.Description = "Multi-sport analytics and historical evidence API.";
    return Task.CompletedTask;
}));
var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/swagger/{documentName}/swagger.json");
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "BetStats API v1"));
}

app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }))
    .WithTags("Health").WithSummary("Process liveness").WithDescription("Existing liveness response; does not test database readiness.");
app.MapGet("/api/v1/health", () => TypedResults.Ok(new ApiHealth("Healthy")))
    .WithTags("Health").WithSummary("API process liveness").WithDescription("Returns process status without accessing PostgreSQL. This is not a readiness check.")
    .ProducesProblem(500);
app.MapGet("/api/v1/sports", Results<Ok<PublicSportPage>, ProblemHttpResult> (int? offset, int? limit, CancellationToken token) =>
{
    token.ThrowIfCancellationRequested();
    try { return TypedResults.Ok(PublicSportCatalog.Read(offset ?? 0, limit ?? 20)); }
    catch (ArgumentException) { return TypedResults.Problem(statusCode: 400, title: "Invalid pagination", detail: "Offset must be 0..10000 and limit 1..100."); }
}).WithTags("Reference data").WithSummary("List project-owned sport codes")
    .WithDescription("Stable ordinal code ordering. Optional offset defaults to 0 and limit to 20 (maximum 100). Returns project-owned reference vocabulary only; no provider-derived canonical rows or audit metadata.")
    .ProducesProblem(400).ProducesProblem(500);

app.Run();

public sealed record ApiHealth(string Status);
public partial class Program;
