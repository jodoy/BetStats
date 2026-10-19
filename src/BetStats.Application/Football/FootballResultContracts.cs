using BetStats.Application.Datasets;
using BetStats.Application.Governance;
using BetStats.Domain.Football;
using BetStats.Domain.Governance;
using BetStats.Domain.Quality;
using BetStats.Application.Ingestion;

namespace BetStats.Application.Football;

public sealed record FootballResultQuery(Guid CompetitionId, Guid SeasonId, DateTime AsOfUtc,
    DataPurpose Purpose, UsageContext Context, Guid? EventId = null, Guid? SourceId = null,
    DatasetMode Mode = DatasetMode.HistoricalAsKnown, DateTime? ReconstructionAtUtc = null, bool IncludeSuperseded = false);
public sealed record FootballResultEvidence(FootballResultObservation Observation, bool Eligible,
    IReadOnlyList<string> Reasons, IReadOnlyList<Guid> IdentityDecisionIds, IReadOnlyList<Guid> QualityIds,
    IReadOnlyList<DatasetFrozenRecord> FrozenRecords, FootballOutcomeLabels? Labels);
public sealed record FootballResultReport(FootballResultQuery Query, IReadOnlyList<FootballResultEvidence> Results);
public interface IFootballResults
{
    Task<FootballResultReport> ReadAsync(FootballResultQuery query, CancellationToken token = default);
    async Task<IReadOnlyList<FootballResultReport>> ReadManyAsync(IReadOnlyList<FootballResultQuery> queries, CancellationToken token = default)
    {
        if (queries.Count > 100) throw new ArgumentException("Result batch exceeds 100.");
        var reports = new List<FootballResultReport>();
        foreach (var query in queries) reports.Add(await ReadAsync(query, token));
        return reports;
    }
    Task<IReadOnlyList<FootballResultReport>> ReadManyAsync(IReadOnlyList<FootballResultQuery> queries, RawReadSession session, CancellationToken token = default) => ReadManyAsync(queries, token);
    Task EnsureCurrentAsync(IEnumerable<FootballResultEvidence> evidence, DataPurpose purpose, UsageContext context, CancellationToken token = default);
}
