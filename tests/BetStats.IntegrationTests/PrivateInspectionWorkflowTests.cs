using System.Net;
using BetStats.Application.Governance;
using BetStats.Infrastructure.Pipeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.IntegrationTests;

[Trait("Category", "Security")]
public sealed class PrivateInspectionWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class Loopback : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        { app.Use((context, following) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return following(); }); next(app); };
    }
    private sealed class Host(string connection, string raw, string environment = "Development") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(environment)
            .UseSetting("ConnectionStrings:BetStats", connection).UseSetting("Ingestion:RawStoragePath", raw)
            .ConfigureServices(s => s.AddSingleton<IStartupFilter, Loopback>());
    }
    [Fact]
    public async Task Private_receipt_reads_require_network_boundary_and_current_rights_without_raw_or_secrets()
    {
        var f = await new ProviderPublicationWorkflowTests(fixture).Create(privateOnly: true); await using var s = f.Scenario;
        var raw = await ProviderPublicationWorkflowTests.Capture(f); var artifact = await ProviderPublicationWorkflowTests.Publish(f, raw);
        var publisher = s.Get<ProviderPublication>();
        Assert.True((await publisher.VerifyAsync(artifact.Report.ReceiptId, InspectionAudience.PrivateOperatorInspection)).Verified);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => publisher.VerifyAsync(artifact.Report.ReceiptId, true));
        await using var host = new Host(fixture.GetConnectionString(), s.Root); using var client = host.CreateClient(new() { BaseAddress = new("http://localhost") });
        var path = "/api/v1/dashboard/publication/" + artifact.Report.ReceiptId;
        using var read = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var text = await read.Content.ReadAsStringAsync(); Assert.DoesNotContain("storageKey", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x-apisports-key", text, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("\"response\"", text);
        using var foreign = new HttpRequestMessage(HttpMethod.Get, path); foreign.Headers.Add("Origin", "https://example.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);
        using var badHost = new HttpRequestMessage(HttpMethod.Get, path); badHost.Headers.Host = "example.invalid";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(badHost)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/publication/" + artifact.Report.ReceiptId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        await using var prod = new Host(fixture.GetConnectionString(), s.Root, "Production"); using var production = prod.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Forbidden, (await production.GetAsync(path)).StatusCode);
        var policy = await s.Db.SourcePolicies.Include(p => p.Audit).SingleAsync(p => p.Id == f.PolicyId);
        policy.Revoke(Guid.NewGuid(), "test:operator", "Immediate revocation", await s.Now()); await s.Db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
    }
}
