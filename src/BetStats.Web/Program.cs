using BetStats.Application.Dashboard;
using BetStats.Web.Components;

namespace BetStats.Web;

public sealed class WebProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddHttpClient<DashboardClient>(client =>
        {
            client.BaseAddress = DashboardAccess.ApiUri(builder.Configuration["Dashboard:ApiBaseUrl"] ?? "http://localhost:5080/");
            client.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
            if (!DashboardAccess.Allowed(app.Environment.IsDevelopment(), context.Connection.RemoteIpAddress,
                context.Request.Host.Host, context.Request.Headers.Origin.ToString()))
            { context.Response.StatusCode = 403; return; }
            await next();
        });
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.Run();
    }
}
