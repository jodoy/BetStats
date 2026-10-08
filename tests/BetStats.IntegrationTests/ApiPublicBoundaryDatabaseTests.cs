using System.Net;
using BetStats.Application.Governance;
using BetStats.Domain.Governance;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace BetStats.IntegrationTests;

public sealed class ApiPublicBoundaryDatabaseTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class Host(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development").UseSetting("ConnectionStrings:BetStats", connection);
    }
    [Fact]
    public async Task Canonical_presence_does_not_grant_public_display_or_expand_reference_catalog()
    {
        await using var db = fixture.CreateContext();
        var source = new DataSource { Id = Guid.NewGuid(), Code = "private-audit-" + Guid.NewGuid().ToString("N"), DisplayName = "Fictional private source", IsEnabled = true, CreatedAtUtc = DateTime.UtcNow };
        var sport = new Sport(Guid.NewGuid(), "private-" + Guid.NewGuid().ToString("N"), "Fictional private projection");
        db.AddRange(source, sport); await db.SaveChangesAsync();
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        var rights = await new SourcePolicyEvaluator(new SourcePolicyHistory(db)).EvaluateAsync(source.Id, DataPurpose.PublicDisplay, now, new());
        Assert.False(rights.Allowed);
        await using var host = new Host(fixture.GetConnectionString()); using var client = host.CreateClient();
        var body = await client.GetStringAsync("/api/v1/sports");
        Assert.DoesNotContain(sport.Code, body); Assert.DoesNotContain(source.Code, body);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/coverage/report")).StatusCode);
    }
}
