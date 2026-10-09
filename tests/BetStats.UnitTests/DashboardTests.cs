using System.Net;
using BetStats.Application.Dashboard;

namespace BetStats.UnitTests;

public sealed class DashboardTests
{
    [Theory]
    [InlineData(true, "127.0.0.1", "localhost", true)]
    [InlineData(true, "::1", "[::1]", true)]
    [InlineData(false, "127.0.0.1", "localhost", false)]
    [InlineData(true, "192.168.1.2", "localhost", false)]
    [InlineData(true, "127.0.0.1", "evil.example", false)]
    public void Local_development_only(bool development, string ip, string host, bool expected) =>
        Assert.Equal(expected, DashboardAccess.Allowed(development, IPAddress.Parse(ip), host));
    [Fact]
    public void Foreign_origin_and_unidentified_connections_are_denied()
    {
        Assert.False(DashboardAccess.Allowed(true, IPAddress.Loopback, "localhost", "https://evil.example"));
        Assert.False(DashboardAccess.Allowed(true, null, "localhost"));
    }
    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("http://user:secret@localhost/")]
    [InlineData("file:///tmp/x")]
    [InlineData("http://localhost/?token=x")]
    public void Unsafe_api_targets_are_rejected(string uri) => Assert.Throws<ArgumentException>(() => DashboardAccess.ApiUri(uri));
    [Theory]
    [InlineData(-1, 20)]
    [InlineData(0, 0)]
    [InlineData(10001, 20)]
    [InlineData(0, 101)]
    public void Pagination_is_bounded(int offset, int limit) => Assert.Throws<ArgumentException>(() => new DashboardFilter(Offset: offset, Limit: limit).Validate());
    [Fact]
    public void Unknown_filters_fail_closed()
    {
        Assert.Throws<ArgumentException>(() => new DashboardFilter(Status: "MadeUp").Validate());
        Assert.Throws<ArgumentException>(() => new DashboardFilter(From: new(2026, 2, 2), To: new(2026, 1, 1)).Validate());
    }
    [Theory]
    [InlineData("pl-PL", "Niedostępne")]
    [InlineData("en-GB", "Unavailable")]
    public void Missing_and_ineligible_metrics_are_never_zero(string language, string unavailable)
    {
        Assert.Equal(unavailable, DashboardPresentation.Metric(null, false, true, language));
        Assert.Equal(unavailable, DashboardPresentation.Metric(0, false, false, language));
        Assert.Equal("+∞", DashboardPresentation.Metric(null, true, true, language));
    }
    [Fact]
    public async Task Demo_is_explicit_filtered_and_never_claims_observed_metrics()
    {
        var q = new DemoDashboardQueries(); var first = await q.FixturesAsync(new(Limit: 2)); var second = await q.FixturesAsync(new(Offset: 2, Limit: 2));
        Assert.True(first.Demo && first.HasMore); Assert.Empty(first.Items.Select(r => r.EventId).Intersect(second.Items.Select(r => r.EventId)));
        var completed = await q.FixturesAsync(new(Status: "Completed")); Assert.Single(completed.Items); Assert.Equal(2, completed.Items[0].HomeScore);
        Assert.Empty((await q.BacktestsAsync(new())).Items); Assert.False((await q.QualityAsync(new())).CurrentlyAuthorized);
        Assert.Empty((await q.FixturesAsync(new(Competition: Guid.NewGuid()))).Items);
    }
}
