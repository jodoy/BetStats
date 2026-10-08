using BetStats.Application.Datasets;
using BetStats.Application.Governance;
using BetStats.Domain.Football;
using BetStats.Domain.Governance;
using BetStats.Domain.Quality;

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
    Task EnsureCurrentAsync(IEnumerable<FootballResultEvidence> evidence, DataPurpose purpose, UsageContext context, CancellationToken token = default);
}
