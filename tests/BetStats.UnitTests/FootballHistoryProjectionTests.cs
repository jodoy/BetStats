using System.Text;
using BetStats.Application.Ingestion;

namespace BetStats.UnitTests;

public sealed class FootballHistoryProjectionTests
{
    // Project-authored fictional rows in a football-data style layout with extra columns.
    private const string Export =
        "Div,Date,Time,HomeTeam,AwayTeam,FTHG,FTAG,FTR,HTHG,HTAG,HTR,Referee,B365H,B365D,B365A\n" +
        "XL,05/08/23,15:00,Amber Comets,Cobalt Owls,2,1,H,1,0,H,A Person,1.50,4.00,6.00\n" +
        "XL,12/08/2023,17:30,Cobalt Owls,Silver Foxes,0,0,D,0,0,D,B Person,2.10,3.20,3.60\n" +
        "XM,12/08/23,15:00,Violet Herons,Amber Comets,1,1,D,0,0,D,C Person,2.00,3.00,4.00\n" +
        "XL,13/01/24,,\"Silver Foxes, Reserve\",Amber Comets,1,3,A,0,1,A,,3.00,3.30,2.20\n" +
        ",,,,,,,,,,,,,,\n";

    private static readonly FootballImportScope Scope = new("XL", "2023-2024");

    [Fact]
    public void Projection_keeps_only_profile_columns_and_the_selected_competition()
    {
        var result = FootballHistoryProjection.Project(Encoding.UTF8.GetBytes(Export), Scope);
        var lines = Encoding.UTF8.GetString(result.Content).TrimEnd('\n').Split('\n');

        Assert.Equal("Div,Date,Time,HomeTeam,AwayTeam,FTHG,FTAG,HTHG,HTAG,FTR,HTR", lines[0]);
        Assert.Equal("XL,05/08/2023,15:00,Amber Comets,Cobalt Owls,2,1,1,0,H,H", lines[1]);
        Assert.Equal("XL,12/08/2023,17:30,Cobalt Owls,Silver Foxes,0,0,0,0,D,D", lines[2]);
        Assert.Equal("XL,13/01/2024,,\"Silver Foxes, Reserve\",Amber Comets,1,3,0,1,A,A", lines[3]);
        Assert.Equal(4, lines.Length);
        Assert.Equal(4, result.InputRows);
        Assert.Equal(3, result.ProjectedRows);
        Assert.Equal(1, result.OtherCompetitionRows);
        Assert.Empty(result.Issues);
        Assert.DoesNotContain("B365", Encoding.UTF8.GetString(result.Content), StringComparison.Ordinal);
    }

    [Fact]
    public void Projection_is_deterministic()
    {
        var first = FootballHistoryProjection.Project(Encoding.UTF8.GetBytes(Export), Scope);
        var second = FootballHistoryProjection.Project(Encoding.UTF8.GetBytes(Export), Scope);

        Assert.Equal(first.Content, second.Content);
    }

    [Theory]
    [InlineData("05/08/23", 2023, "05/08/2023")]
    [InlineData("13/01/24", 2023, "13/01/2024")]
    [InlineData("01/01/00", 1999, "01/01/2000")]
    [InlineData("20/08/99", 1999, "20/08/1999")]
    [InlineData("05/08/2023", 2023, "05/08/2023")]
    public void Two_digit_years_expand_from_the_explicit_season_start(string value, int startYear, string expected)
    {
        Assert.Equal(expected, FootballHistoryProjection.NormalizeDate(value, startYear));
    }

    [Theory]
    [InlineData("31/02/23")]
    [InlineData("2023-08-05")]
    [InlineData("5/8/23")]
    [InlineData("")]
    public void Invalid_dates_are_rejected(string value)
    {
        Assert.Null(FootballHistoryProjection.NormalizeDate(value, 2023));
    }

    [Fact]
    public void Invalid_date_rows_are_reported_and_skipped()
    {
        var csv = "Div,Date,HomeTeam,AwayTeam,FTHG,FTAG\nXL,not-a-date,Amber Comets,Cobalt Owls,1,0\n";
        var result = FootballHistoryProjection.Project(Encoding.UTF8.GetBytes(csv), Scope);

        Assert.Equal(0, result.ProjectedRows);
        Assert.Equal("invalid_date", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Missing_required_columns_are_rejected()
    {
        var csv = "Div,Date,HomeTeam,AwayTeam,FTHG\nXL,05/08/23,Amber Comets,Cobalt Owls,1\n";

        var error = Assert.Throws<InvalidDataException>(() => FootballHistoryProjection.Project(Encoding.UTF8.GetBytes(csv), Scope));
        Assert.Equal("invalid_headers", error.Message);
    }

    [Fact]
    public void Season_reference_must_start_with_a_year()
    {
        var csv = Encoding.UTF8.GetBytes("Div,Date,HomeTeam,AwayTeam,FTHG,FTAG\n");

        Assert.Throws<ArgumentException>(() => FootballHistoryProjection.Project(csv, new("XL", "current")));
    }

    [Fact]
    public void Unterminated_quotes_are_malformed()
    {
        var csv = Encoding.UTF8.GetBytes("Div,Date,HomeTeam,AwayTeam,FTHG,FTAG\nXL,05/08/23,\"Amber,Cobalt Owls,1,0\n");

        var error = Assert.Throws<InvalidDataException>(() => FootballHistoryProjection.Project(csv, Scope));
        Assert.Equal("malformed_csv", error.Message);
    }
}
