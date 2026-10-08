using BetStats.Domain.Quality;
using BetStats.Domain.Sports;

namespace BetStats.Application.Quality;

public sealed record FootballQualityEvidence(Guid SportId, Guid? CompetitionSportId, Guid? SeasonCompetitionId, Guid? CompetitionId,
    DateOnly Date, DateOnly? SeasonStart, DateOnly? SeasonEnd, Guid? HomeId, Guid? AwayId, Guid? HomeSportId, Guid? AwaySportId,
    bool IdentityMissing, bool CanonicalMismatch, bool ConflictingAssignments, DateOnly? PreviousDate,
    SportingEventStatus? PreviousStatus, SportingEventStatus? Status, bool LaterEvidence, bool ChainValid, bool ProvenanceValid,
    bool StaleReplay = false, bool SimultaneousContradiction = false, bool IndependentSourceConflict = false);

public static class FootballQualityRules
{
    public const int Version = 1;
    public static readonly Guid Football = ReferenceSports.All.Single(s => s.Code == "football").Id;
    public static readonly IReadOnlyList<QualityRule> Catalog = new[] { "sport", "competition-season", "event-date", "season-interval", "different-participants",
        "participant-sport", "canonical-identity", "event-collision", "home-away", "event-status", "date-conflict", "correction-chain", "provenance", "replay", "cross-source" }
        .Select(id => new QualityRule("football." + id, Version)).ToArray();
    public static IReadOnlyList<QualityIssue> Assess(FootballQualityEvidence e)
    {
        var results = new List<QualityIssue>();
        void Rule(string id, bool pass, string code, QualityClassification classification = QualityClassification.Invalid,
            QualitySeverity severity = QualitySeverity.Error, bool blocks = true) => results.Add(new(new("football." + id, Version), pass,
                pass ? QualitySeverity.Info : severity, !pass && blocks, pass ? "passed" : code, pass ? QualityClassification.Accepted : classification));
        Rule("sport", e.SportId == Football, "unsupported_sport");
        Rule("competition-season", (e.CompetitionSportId is null || e.CompetitionSportId == Football) &&
            (e.SeasonCompetitionId is null || e.CompetitionId is null || e.SeasonCompetitionId == e.CompetitionId), "competition_season_mismatch", QualityClassification.CanonicalMismatch);
        Rule("event-date", e.Date != default, "invalid_event_date");
        Rule("season-interval", (e.SeasonStart is null || e.Date >= e.SeasonStart) && (e.SeasonEnd is null || e.Date <= e.SeasonEnd), "outside_season_interval");
        Rule("different-participants", e.HomeId is null || e.AwayId is null || e.HomeId != e.AwayId, "same_participants", QualityClassification.CanonicalMismatch);
        Rule("participant-sport", (e.HomeSportId is null || e.HomeSportId == Football) && (e.AwaySportId is null || e.AwaySportId == Football), "participant_sport_mismatch", QualityClassification.CanonicalMismatch);
        Rule("canonical-identity", !e.IdentityMissing, "identity_unresolved", QualityClassification.IdentityAmbiguous, QualitySeverity.Warning);
        Rule("event-collision", !e.CanonicalMismatch, "event_identity_collision", QualityClassification.CanonicalMismatch, QualitySeverity.Critical);
        Rule("home-away", !e.ConflictingAssignments, "home_away_conflict", QualityClassification.CanonicalMismatch);
        var transition = e.PreviousStatus is not { } old || e.Status is not { } next || old == next ||
            (old, next) is (SportingEventStatus.Scheduled, SportingEventStatus.InProgress or SportingEventStatus.Completed or SportingEventStatus.Postponed or SportingEventStatus.Cancelled)
                or (SportingEventStatus.InProgress, SportingEventStatus.Completed or SportingEventStatus.Postponed or SportingEventStatus.Cancelled)
                or (SportingEventStatus.Postponed, SportingEventStatus.Scheduled or SportingEventStatus.Completed or SportingEventStatus.Cancelled);
        Rule("event-status", transition, "invalid_status_transition", QualityClassification.InvalidTransition);
        var changedDate = e.PreviousDate is { } date && date != e.Date;
        Rule("date-conflict", e.StaleReplay || (!e.SimultaneousContradiction && (!changedDate || e.LaterEvidence)), "contradictory_event_date", QualityClassification.ObservationConflict);
        if (changedDate && e.LaterEvidence && !e.SimultaneousContradiction && !e.StaleReplay)
            results[^1] = new(new("football.date-conflict", Version), true, QualitySeverity.Info, false, "historical_correction", QualityClassification.HistoricalCorrection);
        Rule("correction-chain", e.ChainValid, "correction_chain_invalid", QualityClassification.ObservationConflict, QualitySeverity.Critical);
        Rule("provenance", e.ProvenanceValid, "provenance_incomplete", QualityClassification.Invalid, QualitySeverity.Critical);
        Rule("replay", !e.StaleReplay, "older_evidence_replay", QualityClassification.Superseded, QualitySeverity.Info);
        Rule("cross-source", !e.IndependentSourceConflict, "cross_source_observation_conflict", QualityClassification.ObservationConflict, QualitySeverity.Warning);
        return results;
    }
    public static QualityIssue ParseIssue(string code) => new(new("football.source-validation", Version), false,
        code == "duplicate_reference" ? QualitySeverity.Info : QualitySeverity.Error, true, code,
        code == "duplicate_reference" ? QualityClassification.DuplicateEquivalent : code == "identity_collision" ? QualityClassification.DuplicateContradictory : QualityClassification.Invalid);
}
