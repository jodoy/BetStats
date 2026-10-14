using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Ingestion;

namespace BetStats.IntegrationTests;

// Controlled fictional JSON. These are mapping contract tests, not DB publication tests.
public sealed class ApiFootballPublicationMappingTests
{
    private static JsonObject Payload(string status = "NS") => JsonNode.Parse("""
        {"get":"fixtures","errors":[],"results":1,"paging":{"current":1,"total":1},
        "response":[{"fixture":{"id":999001,"timezone":"UTC","date":"2026-10-15T18:00:00Z",
        "timestamp":1792087200,"status":{"short":"NS"}},"league":{"id":999999,"season":2026},
        "teams":{"home":{"id":99901},"away":{"id":99902}},"goals":{"home":null,"away":null},
        "score":{"fulltime":{"home":null,"away":null},"halftime":{"home":null,"away":null}}}]}
        """)!.AsObject().SetStatus(status);
    private static byte[] Bytes(JsonObject json) => Encoding.UTF8.GetBytes(json.ToJsonString());
    private static ApiFootballPublicationContext Context(byte[] bytes) => new(1, Guid.NewGuid(), "api-football",
        ApiFootballFixtureAdapter.Profile, Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(bytes)),
        new("999999", "2026"), Guid.NewGuid(), 1, new(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc),
        new(2026, 10, 14, 0, 0, 1, DateTimeKind.Utc), new(1, 999999, 2026, new(2026, 10, 13), new(2026, 10, 19)));
    private static ApiFootballMappedFixture Map(JsonObject json)
    { var bytes = Bytes(json); return Assert.Single(ApiFootballPublicationMapping.Map(bytes, Context(bytes))); }

    [Theory]
    [InlineData("NS", SportingEventStatus.Scheduled)]
    [InlineData("1H", SportingEventStatus.InProgress)]
    [InlineData("HT", SportingEventStatus.InProgress)]
    [InlineData("2H", SportingEventStatus.InProgress)]
    [InlineData("ET", SportingEventStatus.InProgress)]
    [InlineData("BT", SportingEventStatus.InProgress)]
    [InlineData("P", SportingEventStatus.InProgress)]
    [InlineData("LIVE", SportingEventStatus.InProgress)]
    [InlineData("PST", SportingEventStatus.Postponed)]
    [InlineData("CANC", SportingEventStatus.Cancelled)]
    [InlineData("FT", SportingEventStatus.Completed)]
    [InlineData("AET", SportingEventStatus.Completed)]
    [InlineData("PEN", SportingEventStatus.Completed)]
    public void Metadata_status_is_independent_of_final_result(string status, SportingEventStatus expected)
    {
        var row = Map(Payload(status)); Assert.Equal(expected, row.Status);
        Assert.Empty(row.MetadataExclusions); Assert.Null(row.FinalResult);
        Assert.NotEmpty(row.ResultExclusions);
    }

    [Theory] [InlineData("TBD")] [InlineData("SUSP")] [InlineData("INT")]
    [InlineData("ABD")] [InlineData("AWD")] [InlineData("WO")]
    public void Unsupported_canonical_status_never_guesses_completion(string status)
    {
        var row = Map(Payload(status)); Assert.Null(row.Status); Assert.Null(row.FinalResult);
        Assert.Equal(status, row.ProviderStatus); Assert.Contains("unsupported_canonical_status", row.MetadataExclusions);
    }

    [Fact]
    public void Exact_ids_and_precise_time_survive_mapping_without_name_matching()
    {
        var row = Map(Payload()); Assert.Equal("provider:api-football:fixture:999001", row.EventReference);
        Assert.Equal("provider:api-football:team:99901", row.HomeReference);
        Assert.Equal("provider:api-football:team:99902", row.AwayReference);
        Assert.Equal(new DateTime(2026, 10, 15, 18, 0, 0, DateTimeKind.Utc), row.KickoffUtc);
        Assert.Equal("provider:competition:999999", row.CompetitionReference);
    }

    [Fact]
    public void Full_time_result_does_not_require_invented_half_time_scores()
    {
        var json = Payload("FT"); var item = json["response"]![0]!;
        item["goals"]!["home"] = 2; item["goals"]!["away"] = 1;
        item["score"]!["fulltime"]!["home"] = 2; item["score"]!["fulltime"]!["away"] = 1;
        var row = Map(json); Assert.NotNull(row.FinalResult); Assert.Equal(2, row.FinalResult.FullTime.Home);
        Assert.Null(row.FinalResult.HalfTime.Home); Assert.Contains("missing_half_time_result", row.ResultExclusions);
    }

    [Theory] [InlineData("hash")] [InlineData("provider")] [InlineData("profile")]
    [InlineData("source")] [InlineData("raw")] [InlineData("policy")] [InlineData("policy-version")]
    [InlineData("scope")] [InlineData("clock")] [InlineData("precision")]
    public void Immutable_context_mismatches_are_rejected(string fault)
    {
        var bytes = Bytes(Payload()); var c = Context(bytes);
        c = fault switch
        {
            "hash" => c with { RawHash = new('0', 64) }, "provider" => c with { Provider = "other" },
            "profile" => c with { Profile = "metadata-v1" }, "source" => c with { SourceId = Guid.Empty },
            "raw" => c with { RawId = Guid.Empty }, "policy" => c with { PolicyId = Guid.Empty },
            "policy-version" => c with { PolicyVersion = 0 }, "scope" => c with { OriginalScope = new("999999", "2025") },
            "clock" => c with { RecordedUtc = c.RetrievedUtc.AddSeconds(-1) },
            _ => c with { RetrievedUtc = c.RetrievedUtc.AddTicks(1) }
        };
        Assert.Throws<InvalidDataException>(() => ApiFootballPublicationMapping.Map(bytes, c));
    }

    [Theory] [InlineData("league")] [InlineData("season")] [InlineData("unknown-status")]
    [InlineData("same-team")] [InlineData("pagination")] [InlineData("duplicate")]
    public void Existing_strict_reader_remains_the_schema_boundary(string fault)
    {
        var json = Payload(); var item = json["response"]![0]!;
        switch (fault)
        {
            case "league": item["league"]!["id"] = 1; break;
            case "season": item["league"]!["season"] = 2025; break;
            case "unknown-status": item["fixture"]!["status"]!["short"] = "UNKNOWN"; break;
            case "same-team": item["teams"]!["away"]!["id"] = 99901; break;
            case "pagination": json["paging"]!["total"] = 2; break;
            default: json["response"]!.AsArray().Add(item.DeepClone()); json["results"] = 2; break;
        }
        Assert.Throws<InvalidDataException>(() => Map(json));
    }

    [Fact]
    public void Rescheduling_and_score_corrections_are_separate_mapping_inputs()
    {
        var original = Payload("FT"); var item = original["response"]![0]!;
        item["goals"]!["home"] = 2; item["goals"]!["away"] = 1;
        item["score"]!["fulltime"]!["home"] = 2; item["score"]!["fulltime"]!["away"] = 1;
        var before = Bytes(original); var first = Map(original);
        var correction = original.DeepClone().AsObject(); item = correction["response"]![0]!;
        item["fixture"]!["date"] = "2026-10-15T19:00:00Z"; item["fixture"]!["timestamp"] = 1792090800;
        item["goals"]!["home"] = 3; item["score"]!["fulltime"]!["home"] = 3;
        var next = Map(correction);
        Assert.Equal(first.EventReference, next.EventReference); Assert.NotEqual(first.KickoffUtc, next.KickoffUtc);
        Assert.Equal(2, first.FinalResult!.FullTime.Home); Assert.Equal(3, next.FinalResult!.FullTime.Home);
        Assert.Equal(before, Bytes(original));
    }

    [Theory] [InlineData("{corrupt")]
    [InlineData("{\"get\":\"fixtures\",\"get\":\"fixtures\"}")]
    public void Corrupted_json_and_duplicate_members_are_rejected(string original)
    {
        var bytes = Encoding.UTF8.GetBytes(original);
        Assert.Throws<InvalidDataException>(() => ApiFootballPublicationMapping.Map(bytes, Context(bytes)));
    }

    [Fact]
    public void Conflicting_kickoff_prevents_metadata_eligibility_without_inventing_a_time()
    {
        var json = Payload(); json["response"]![0]!["fixture"]!["timestamp"] = 1;
        var row = Map(json); Assert.Null(row.KickoffUtc);
        Assert.Contains("missing_or_conflicting_kickoff_precision", row.MetadataExclusions);
    }

    [Fact]
    public void Repeated_mapping_is_deterministic_and_does_not_rewrite_raw()
    {
        var bytes = Bytes(Payload()); var original = bytes.ToArray(); var c = Context(bytes);
        var first = ApiFootballPublicationMapping.Map(bytes, c); var second = ApiFootballPublicationMapping.Map(bytes, c);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void Bound_kickoff_claim_json_retains_legacy_profile_instead_of_becoming_provider_json()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"Version\":1,\"OriginalRawId\":\"00000000-0000-0000-0000-000000000001\",\"Value\":{}}");
        Assert.Equal(FootballDataCsvParser.Version, FootballFixtureParser.Profile(bytes));
        Assert.Equal(ApiFootballFixtureAdapter.Profile, FootballFixtureParser.Profile(Bytes(Payload())));
    }
    [Fact]
    public void Cancellation_is_observed_before_interpreting_original_raw()
    {
        var bytes = Bytes(Payload()); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ApiFootballPublicationMapping.Map(bytes, Context(bytes), cancelled.Token));
    }
}

internal static class PublicationJsonFixture
{
    internal static JsonObject SetStatus(this JsonObject json, string status)
    { json["response"]![0]!["fixture"]!["status"]!["short"] = status; return json; }
}
