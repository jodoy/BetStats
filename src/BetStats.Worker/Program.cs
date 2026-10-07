using BetStats.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPersistence(builder.Configuration);
var host = builder.Build();
host.Run();
