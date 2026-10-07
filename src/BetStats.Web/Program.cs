var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "BetStats 2.0");

app.Run();
