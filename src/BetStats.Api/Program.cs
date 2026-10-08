using BetStats.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPersistence(builder.Configuration);
var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }));

app.Run();
