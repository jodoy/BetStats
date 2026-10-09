using BetStats.Domain.Football;

namespace BetStats.Application.Ingestion;

public sealed record FootballReadinessSeason(Guid CompetitionId, Guid SeasonId, string League, string Season,
    int Events, int UnknownFullTimeScores, int RegulationLabelSamples);
public sealed record FootballReadinessSample(Guid EventId, DateOnly EventDate, Guid RawId, DateTime? PublishedAtUtc,
    DateTime RetrievedAtUtc, DateTime RecordedAtUtc, FootballResultValue Value, bool RegulationLabelEligible,
    string RecordingEvidence, string RawEvidence);
public sealed record FootballReadinessReport(string Version, Guid SourceId, IReadOnlyList<FootballReadinessSeason> Seasons,
    IReadOnlyList<FootballReadinessSample> HistoricalSamples, bool Truncated, int CertifiedUsableFeatureWindows,
    double? OperationalHistoricalAccuracy, string MissingFixtures, string MetadataCoverage, string ResultCoverage,
    IReadOnlyList<string> Exclusions);
