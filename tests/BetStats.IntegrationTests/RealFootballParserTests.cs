using System.Text;
using BetStats.Application.Football;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Infrastructure.Ingestion;

namespace BetStats.IntegrationTests;

public sealed class RealFootballParserTests
{
    private const string Header = "Div,Date,HomeTeam,AwayTeam,FTHG,FTAG,HTHG,HTAG,MatchId,Status,ResultBasis\n";
    private static FootballParseResult Parse(string text) => new HistoricalFootballCsvParser().Parse(Encoding.UTF8.GetBytes(text), new("E0", "2020-2021"));
    [Fact]
    public void Standard_history_preserves_nulls_ids_and_absent_publication_clock()
    {
        var parsed = Parse(Header + "E0,12/09/2020,Home,Away,2,1,1,0,match-1,Finished,RegulationTime\n" +
            "E0,13/09/2020,Other,Away,unknown,unknown,,,match-2,Postponed,Unknown\n");
        Assert.Empty(parsed.Issues); Assert.Equal(HistoricalFootballCsvParser.Version, parsed.ParserVersion);
        Assert.Equal("provider:match-1", parsed.Records[0].MatchReference);
        Assert.Equal(2, parsed.Records[0].Result!.Value.FullTime.Home); Assert.Null(parsed.Records[0].Result!.PublishedAtUtc);
        Assert.Null(parsed.Records[1].Result!.Value.FullTime.Home); Assert.False(FootballResultRules.LabelEligible(parsed.Records[1].Result!.Value));
    }
    [Theory]
    [InlineData("Postponed", "Unknown")]
    [InlineData("Cancelled", "RegulationTime")]
    [InlineData("Abandoned", "RegulationTime")]
    [InlineData("Finished", "IncludesExtraTime")]
    public void Unsupported_regulation_labels_are_never_created(string status, string basis)
    {
        var row = Assert.Single(Parse(Header + $"E0,12/09/2020,Home,Away,2,1,1,0,m,{status},{basis}\n").Records);
        Assert.False(FootballResultRules.LabelEligible(row.Result!.Value));
    }
    [Theory]
    [InlineData("E0,12/09/20,Home,Away,2,1,1,0,m,Finished,RegulationTime", "invalid_date")]
    [InlineData("E1,12/09/2020,Home,Away,2,1,1,0,m,Finished,RegulationTime", "unsupported_competition")]
    [InlineData("E0,12/09/2020,Home", "schema_mismatch")]
    [InlineData("E0,12/09/2020,Home,Away,2,1,1,0,m,Mystery,RegulationTime", "invalid_result_status_or_basis")]
    public void Malformed_rows_are_reported(string row, string code)
    {
        var parsed = Parse(Header + row + "\n"); Assert.Empty(parsed.Records);
        Assert.Contains(parsed.Issues, x => x.Code == code); Assert.False(parsed.CompletePayload);
    }
    [Fact]
    public void Same_fixture_with_distinct_provider_ids_is_ambiguous()
    {
        var parsed = Parse(Header + "E0,12/09/2020,Home,Away,2,1,1,0,a,Finished,RegulationTime\n" +
            "E0,12/09/2020,Home,Away,2,1,1,0,b,Finished,RegulationTime\n");
        Assert.Empty(parsed.Records); Assert.Equal(2, parsed.Issues.Count(x => x.Code == "identity_collision"));
    }
    [Fact]
    public void Conflicting_results_remove_both_rows_and_equivalent_duplicates_are_reported()
    {
        const string row = "E0,12/09/2020,Home,Away,2,1,1,0,a,Finished,RegulationTime\n";
        Assert.Single(Parse(Header + row + row).Records);
        var conflicting = Parse(Header + row + row.Replace(",2,1,", ",3,1,", StringComparison.Ordinal));
        Assert.Empty(conflicting.Records); Assert.Contains(conflicting.Issues, x => x.Code == "result_duplicate_conflict");
    }
    [Fact]
    public void Unclosed_partial_file_and_unknown_headers_fail_closed()
    {
        Assert.Empty(Parse(Header + "E0,12/09/2020,\"truncated").Records);
        Assert.Contains(Parse("Div,Date,HomeTeam,AwayTeam,FTHG,FTAG,Odds\n").Issues, x => x.Code == "invalid_headers");
    }
    [Fact]
    public void Existing_metadata_profile_still_ignores_result_columns()
    {
        const string csv = "Div,Date,HomeTeam,AwayTeam,FTHG,FTAG\nE0,12/09/2020,Home,Away,2,1\n";
        var parser = new FootballFixtureParser(); var scope = new FootballImportScope("E0", "2020-2021");
        Assert.Null(Assert.Single(parser.Parse(Encoding.UTF8.GetBytes(csv), scope).Records).Result);
        Assert.NotNull(Assert.Single(parser.ParseProfile(Encoding.UTF8.GetBytes(csv), scope, HistoricalFootballCsvParser.Version).Records).Result);
    }
    [Fact]
    public void Pagination_stops_at_budget_and_repeated_cursors()
    {
        var pagination = new FootballPagination(2, 50); pagination.Next(null); pagination.Next("next");
        Assert.Throws<InvalidOperationException>(() => pagination.Next("last"));
        var repeated = new FootballPagination(10, 50); repeated.Next("same");
        Assert.Throws<InvalidOperationException>(() => repeated.Next("same"));
        Assert.True(FootballPagination.Retryable(new(ProviderErrorCategory.RateLimitExceeded, "429", TimeSpan.FromSeconds(2))));
        Assert.False(FootballPagination.Retryable(new(ProviderErrorCategory.PermissionDenied, "revoked")));
        Assert.True(FootballProviderContract.LocalHistory.IsValid); Assert.False(FootballProviderContract.LocalHistory.SupportsInventory);
    }
}
