using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetStats.Application.Ingestion;

namespace BetStats.Infrastructure.Ingestion;

// Exact BS-005 key format: published receipts remain valid across this extraction.
internal static class FootballPublicationKeys
{
    private static string Hash(string[] parts) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    public static string Batch(FootballImportScope scope, string hash) => Hash([scope.CompetitionReference, scope.SeasonReference, hash, FootballDataCsvParser.Version]);
    public static string Row(string batch, string reference, Guid competition, Guid season, Guid home, Guid away, Guid sportingEvent) =>
        Hash([batch, "row", reference, competition.ToString(), season.ToString(), home.ToString(), away.ToString(), sportingEvent.ToString()]);
}
