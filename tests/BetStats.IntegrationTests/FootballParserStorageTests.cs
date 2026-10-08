using System.Security.Cryptography;
using System.Text;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Infrastructure.Ingestion;

namespace BetStats.IntegrationTests;

public sealed class FootballParserStorageTests
{
    private static FootballParseResult Parse(string csv) => new FootballDataCsvParser().Parse(Encoding.UTF8.GetBytes(csv), SyntheticFootballDemo.Scope);
    [Fact]
    public void Synthetic_fixture_reports_invalid_duplicate_and_valid_rows_explicitly()
    {
        var result = Parse(SyntheticFootballDemo.Csv);
        Assert.Equal(6, result.ParsedCount); Assert.Equal(4, result.Records.Count);
        Assert.Contains(result.Issues, i => i.Code == "invalid_date"); Assert.Contains(result.Issues, i => i.Code == "duplicate_reference");
        Assert.All(result.Records, r => Assert.False(r.CompositeMatchReference));
        Assert.Equal(new DateOnly(2026, 1, 2), result.Records[0].MatchDate);
    }
    [Theory]
    [InlineData("Div,Date,HomeTeam\n", "invalid_headers")]
    [InlineData("Div,Date,HomeTeam,AwayTeam,Surprise\n", "invalid_headers")]
    [InlineData("Div,Date,HomeTeam,AwayTeam,Date\n", "invalid_headers")]
    [InlineData("Div,Date,HomeTeam,AwayTeam\nFICT,02/01/2026,,Fictional Owls\n", "missing_or_invalid_values")]
    [InlineData("Div,Date,HomeTeam,AwayTeam\nOTHER,02/01/2026,Fictional Foxes,Fictional Owls\n", "unsupported_competition")]
    [InlineData("Div,Date,HomeTeam,AwayTeam\nFICT,31/02/2026,Fictional Foxes,Fictional Owls\n", "invalid_date")]
    [InlineData("Div,Date,HomeTeam,AwayTeam,FTR\nFICT,02/01/2026,Fictional Foxes,Fictional Owls,UNKNOWN\n", "invalid_status")]
    [InlineData("Div,Date,HomeTeam,AwayTeam,Time\nFICT,02/01/2026,Fictional Foxes,Fictional Owls,99:20\n", "invalid_time")]
    [InlineData("Div,Date,HomeTeam,AwayTeam\nFICT,02/01/2026,Fictional Foxes\n", "schema_mismatch")]
    [InlineData("Div,Date,HomeTeam,AwayTeam\nFICT,02/01/2026,\"unclosed,Fictional Owls\n", "malformed_csv")]
    public void Invalid_input_has_structured_codes(string csv, string code) => Assert.Contains(Parse(csv).Issues, i => i.Code == code);
    [Fact]
    public void Composite_references_are_scoped_and_not_provider_issued_ids()
    {
        var csv = "Div,Date,HomeTeam,AwayTeam\nFICT,02/01/26,\"Fictional, Foxes\",Fictional Owls\n";
        var first = Assert.Single(Parse(csv).Records); var repeated = Assert.Single(Parse(csv).Records);
        Assert.True(first.CompositeMatchReference); Assert.Equal(first.MatchReference, repeated.MatchReference);
        Assert.StartsWith("composite:event:", first.MatchReference); Assert.Null(first.Status);
        Assert.NotEqual(FootballDataCsvParser.TeamReference("FICT", first.HomeName), FootballDataCsvParser.TeamReference("OTHER", first.HomeName));
    }
    [Fact]
    public void Conflicting_records_with_one_match_id_reject_both_candidates()
    {
        var csv = "Div,Date,HomeTeam,AwayTeam,FTR,MatchId\nFICT,02/01/2026,Fictional Foxes,Fictional Owls,H,fixture-1\nFICT,03/01/2026,Fictional Foxes,Fictional Owls,H,fixture-1\n";
        var parsed = Parse(csv); Assert.Empty(parsed.Records); Assert.Equal(2, parsed.Issues.Count);
        Assert.All(parsed.Issues, i => Assert.Equal("identity_collision", i.Code));
    }
    [Fact]
    public void Invalid_utf8_and_oversized_csv_fail_without_network_or_unbounded_input()
    {
        var parser = new FootballDataCsvParser(); Assert.Contains(parser.Parse(new byte[] { 0xff, 0xfe }, SyntheticFootballDemo.Scope).Issues, i => i.Code == "invalid_encoding");
        Assert.Contains(parser.Parse(new byte[1_048_577], SyntheticFootballDemo.Scope).Issues, i => i.Code == "invalid_scope_or_size");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => parser.Parse(SyntheticFootballDemo.Bytes(), SyntheticFootballDemo.Scope, cancellation.Token));
    }
    [Fact]
    public async Task Candidate_live_flag_is_rejected_and_fixture_adapter_is_cancellable()
    {
        var adapter = new FootballDataFixtureAdapter(Guid.NewGuid(), SyntheticFootballDemo.Bytes(), TimeProvider.System, liveEnabled: true);
        Assert.False(adapter.ValidateConfiguration().Valid);
        var result = await adapter.ExecuteAsync(new(Guid.NewGuid(), ProviderCapability.HistoricalObservations, new()), CancellationToken.None);
        Assert.False(result.Success); Assert.Null(adapter.Content);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ExecuteAsync(new(Guid.NewGuid(), ProviderCapability.HistoricalObservations, new()), cancellation.Token));
    }

    [Fact]
    public async Task Raw_bytes_hash_staging_finalization_and_no_overwrite_are_verified()
    {
        var root = Path.Combine(Path.GetTempPath(), "betstats-storage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new FileSystemRawPayloadStore(root); var bytes = SyntheticFootballDemo.Bytes();
            var first = await storage.StageAsync(bytes); var second = await storage.StageAsync(bytes);
            Assert.NotEqual(first.StorageKey, second.StorageKey); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), first.Hash);
            Assert.Equal(bytes, (await storage.ReadAsync(first)).ToArray()); Assert.Contains(first.StorageKey, storage.InventoryStaged());
            await storage.FinalizeAsync(first); await storage.FinalizeAsync(first);
            Assert.DoesNotContain(first.StorageKey, storage.InventoryStaged()); Assert.Equal(bytes, (await storage.ReadAsync(first)).ToArray());
            await File.WriteAllBytesAsync(Path.Combine(root, first.StorageKey + ".raw"), new byte[bytes.Length]);
            await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReadAsync(first));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.ReadAsync(first with { StorageKey = "../outside" }));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.StageAsync(new byte[1_048_577]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Storage_rejects_repository_root_and_file_collision()
    {
        var root = Path.Combine(Path.GetTempPath(), "betstats-storage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "BetStats.slnx"), "synthetic");
            Assert.Throws<ArgumentException>(() => new FileSystemRawPayloadStore(root));
            Assert.Throws<ArgumentException>(() => new FileSystemRawPayloadStore("relative/path"));
            File.Delete(Path.Combine(root, "BetStats.slnx"));
            File.WriteAllText(Path.Combine(root, "blocked"), "synthetic");
            Assert.Throws<IOException>(() => new FileSystemRawPayloadStore(Path.Combine(root, "blocked", "child")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
