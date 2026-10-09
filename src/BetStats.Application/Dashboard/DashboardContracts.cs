using System.Globalization;
using BetStats.Application.Evaluation;
using BetStats.Application.Models;

namespace BetStats.Application.Dashboard;

public sealed record DashboardFilter(Guid? Competition = null, Guid? Season = null, Guid? Team = null,
    DateOnly? From = null, DateOnly? To = null, string? Status = null, int Offset = 0, int Limit = 20)
{
    public void Validate()
    {
        if (Offset is < 0 or > 10000 || Limit is < 1 or > 100 || From > To ||
            Competition == Guid.Empty || Season == Guid.Empty || Team == Guid.Empty ||
            Status is not (null or "Scheduled" or "Postponed" or "Completed" or "Unknown" or "Conflict" or "Cancelled" or "InProgress"))
            throw new ArgumentException("Invalid bounded dashboard filter.");
    }
}
public sealed record DashboardPage<T>(IReadOnlyList<T> Items, int Offset, int Limit, bool HasMore, bool Demo = false);
public sealed record DashboardChoice(Guid Id, string Name, Guid? Parent = null);
public sealed record DashboardFixture(Guid EventId, Guid DatasetId, string DatasetHash, Guid CompetitionId, Guid SeasonId,
    Guid HomeId, Guid AwayId, DateOnly Date, string Status, int? HomeScore, int? AwayScore, DateTime CutoffUtc,
    int HistoryCount, int ExcludedCount, string Integrity);
public sealed record DashboardPrediction(Guid EventId, DateTime CutoffUtc, string Predictor, int Version,
    string FeatureHash, string InputHash, string Target, IReadOnlyList<decimal> Probabilities, decimal? ExpectedCount,
    int EvidenceCount, bool? Warmed, bool? HalfWarmed);
public sealed record DashboardMetric(string Target, int Requested, int Eligible, int Excluded, string Name,
    int Version, int Denominator, decimal? Value, bool PositiveInfinity, bool MinimumSamplesMet,
    IReadOnlyList<CalibrationBin> Calibration);
public sealed record DashboardBacktest(Guid Id, string Hash, Guid DatasetId, string DatasetHash, DateTime CutoffUtc,
    string ExecutionKind, string Predictor, int PredictorVersion, FootballModelDefinition? Definition,
    IReadOnlyList<DashboardPrediction> Predictions, IReadOnlyList<DashboardMetric> Metrics,
    IReadOnlyList<ModelForecast> Forecasts, IReadOnlyDictionary<string, int> Exclusions,
    IReadOnlyList<ClassCalibration> ClassCalibration, string Integrity, string ComparisonKey,
    IReadOnlyDictionary<string, IReadOnlyList<Guid>> EligibleEvents);
public sealed record DashboardQuality(string Integrity, bool CurrentlyAuthorized, int Fixtures, int Excluded,
    int MissingHistory, int DateOnly, int UnknownStatus, int Conflicts, string Readiness,
    IReadOnlyList<string> Limitations);
public interface IDashboardQueries
{
    Task<DashboardPage<DashboardFixture>> FixturesAsync(DashboardFilter filter, CancellationToken token = default);
    Task<DashboardPage<DashboardChoice>> ChoicesAsync(string kind, DashboardFilter filter, CancellationToken token = default);
    Task<DashboardPage<DashboardBacktest>> BacktestsAsync(DashboardFilter filter, CancellationToken token = default);
    Task<DashboardQuality> QualityAsync(DashboardFilter filter, CancellationToken token = default);
}
public static class DashboardPresentation
{
    public static string Language(string? language) => language == "en-GB" ? "en-GB" : "pl-PL";
    public static string Metric(decimal? value, bool infinity, bool eligible, string language) => !eligible ? Text("unavailable", language) :
        infinity ? "+∞" : value?.ToString("0.####", CultureInfo.GetCultureInfo(Language(language))) ?? Text("unavailable", language);
    public static string Text(string key, string language) => (Language(language) == "en-GB", key) switch
    {
        (true, "dashboard") => "Dashboard",
        (false, "dashboard") => "Pulpit",
        (true, "matches") => "Matches",
        (false, "matches") => "Mecze",
        (true, "teams") => "Teams",
        (false, "teams") => "Drużyny",
        (true, "models") => "Models",
        (false, "models") => "Modele",
        (true, "backtesting") => "Backtesting",
        (false, "backtesting") => "Testy historyczne",
        (true, "quality") => "Data quality",
        (false, "quality") => "Jakość danych",
        (true, "loading") => "Loading verified evidence…",
        (false, "loading") => "Wczytywanie zweryfikowanych dowodów…",
        (true, "empty") => "No verified artifacts match these filters.",
        (false, "empty") => "Brak zweryfikowanych artefaktów dla tych filtrów.",
        (true, "denied") => "Current display permission is unavailable. Ask the data steward to review source rights.",
        (false, "denied") => "Brak aktualnego prawa do wyświetlania. Wymagany przegląd uprawnień źródła.",
        (true, "error") => "Evidence is unavailable. Check API connectivity and artifact integrity, then retry.",
        (false, "error") => "Dowody są niedostępne. Sprawdź połączenie z API i integralność artefaktów, a następnie spróbuj ponownie.",
        (true, "unavailable") => "Unavailable",
        (false, "unavailable") => "Niedostępne",
        (true, "demo") => "DEMO · fictional presentation data · no observed accuracy",
        (false, "demo") => "DEMO · fikcyjne dane prezentacyjne · brak zaobserwowanej skuteczności",
        (true, "competition") => "Competition",
        (false, "competition") => "Rozgrywki",
        (true, "season") => "Season",
        (false, "season") => "Sezon",
        (true, "team") => "Team",
        (false, "team") => "Drużyna",
        (true, "from") => "From date",
        (false, "from") => "Od dnia",
        (true, "to") => "To date",
        (false, "to") => "Do dnia",
        (true, "apply") => "Apply filters",
        (false, "apply") => "Zastosuj filtry",
        (true, "all") => "All",
        (false, "all") => "Wszystkie",
        (true, "next") => "Next",
        (false, "next") => "Dalej",
        (true, "previous") => "Previous",
        (false, "previous") => "Wstecz",
        (true, "Scheduled") => "Scheduled",
        (false, "Scheduled") => "Zaplanowany",
        (true, "Completed") => "Completed",
        (false, "Completed") => "Zakończony",
        (true, "Postponed") => "Postponed",
        (false, "Postponed") => "Przełożony",
        (true, "Unknown") => "Unknown",
        (false, "Unknown") => "Nieznany",
        (true, "Conflict") => "Conflicting evidence",
        (false, "Conflict") => "Sprzeczne dowody",
        (true, "Cancelled") => "Cancelled",
        (false, "Cancelled") => "Odwołany",
        (true, "InProgress") => "In progress",
        (false, "InProgress") => "W toku",
        (true, "status") => "Status",
        (false, "status") => "Status",
        (true, "dateOnly") => "Date only · kickoff unknown",
        (false, "dateOnly") => "Tylko data · godzina nieznana",
        (true, "details") => "Evidence & predictions",
        (false, "details") => "Dowody i predykcje",
        (true, "history") => "Historical evidence",
        (false, "history") => "Dowody historyczne",
        (true, "excluded") => "Excluded",
        (false, "excluded") => "Wykluczone",
        (true, "cutoff") => "Knowledge cutoff (UTC)",
        (false, "cutoff") => "Granica wiedzy (UTC)",
        (true, "verified") => "Stored integrity verified · current rights checked · no replay on read",
        (false, "verified") => "Integralność zapisu potwierdzona · aktualne prawa sprawdzone · odczyt bez przeliczenia",
        (true, "noPrediction") => "No verified prediction artifact. No model was run.",
        (false, "noPrediction") => "Brak zweryfikowanego artefaktu predykcji. Nie uruchomiono modelu.",
        (true, "scope") => "Snapshot evidence only; source-wide coverage is not certified.",
        (false, "scope") => "Wyłącznie dowody snapshotów; kompletność całego źródła nie jest potwierdzona.",
        (true, "first_100_filtered_fixtures") => "Summary covers the first 100 filtered fixtures.",
        (false, "first_100_filtered_fixtures") => "Podsumowanie obejmuje pierwsze 100 meczów po filtrowaniu.",
        (true, "readiness_not_certified_by_snapshot") => "A snapshot does not certify complete historical coverage.",
        (false, "readiness_not_certified_by_snapshot") => "Snapshot nie potwierdza kompletności danych historycznych.",
        (true, "date_only_kickoff_unknown") => "Date-only precision; kickoff times are unavailable.",
        (false, "date_only_kickoff_unknown") => "Dokładność do dnia; godziny rozpoczęcia są niedostępne.",
        (true, "no_live_model_replay") => "Stored artifacts are checked without rerunning models.",
        (false, "no_live_model_replay") => "Sprawdzane są zapisane artefakty, bez ponownego uruchamiania modeli.",
        (true, "identity_and_correction_inventory_not_certified") => "Source-wide identity gaps and correction inventories require an operator review.",
        (false, "identity_and_correction_inventory_not_certified") => "Luki tożsamości i kompletność korekt całego źródła wymagają przeglądu operatora.",
        (true, "presentation_only") => "Fictional presentation data only.",
        (false, "presentation_only") => "Wyłącznie fikcyjne dane prezentacyjne.",
        (true, "no_observed_accuracy") => "No observed accuracy is claimed.",
        (false, "no_observed_accuracy") => "Brak deklarowanej zaobserwowanej skuteczności.",
        (true, "MatchWinner") => "1X2 (home / draw / away)",
        (false, "MatchWinner") => "1X2 (gospodarze / remis / goście)",
        (true, "BothTeamsScoring") => "BTTS (no / yes)",
        (false, "BothTeamsScoring") => "BTTS (nie / tak)",
        (true, "OverUnder25") => "2.5 goals (under / over)",
        (false, "OverUnder25") => "2,5 gola (poniżej / powyżej)",
        (true, "FirstHalfGoalOccurrence") => "First-half goal (no / yes)",
        (false, "FirstHalfGoalOccurrence") => "Gol w pierwszej połowie (nie / tak)",
        (true, "TotalGoals") => "Total goals",
        (false, "TotalGoals") => "Suma goli",
        (true, "FirstHalfTotalGoals") => "First-half goals",
        (false, "FirstHalfTotalGoals") => "Gole w pierwszej połowie",
        (true, "intersection") => "Common eligible samples (equivalent contracts only)",
        (false, "intersection") => "Wspólne kwalifikujące się próbki (tylko równoważne kontrakty)",
        (true, "expected") => "Expected goals",
        (false, "expected") => "Oczekiwana liczba goli",
        (true, "warmup") => "Warm-up",
        (false, "warmup") => "Rozgrzewka modelu",
        (true, "metric") => "Target / metric", (false, "metric") => "Cel / metryka",
        (true, "sampleCounts") => "Requested / eligible / excluded", (false, "sampleCounts") => "Żądane / kwalifikujące się / wykluczone",
        (true, "value") => "Value", (false, "value") => "Wartość",
        (true, "calibration") => "Calibration / exclusions", (false, "calibration") => "Kalibracja / wykluczenia",
        (true, "exactScores") => "Exact scores", (false, "exactScores") => "Dokładne wyniki",
        (true, "home") => "Home", (false, "home") => "Gospodarze",
        (true, "away") => "Away", (false, "away") => "Goście",
        (true, "discardedMass") => "Discarded probability mass", (false, "discardedMass") => "Odrzucona masa prawdopodobieństwa",
        _ => key
    };
}

// Presentation-only, never persisted or mixed with real artifacts.
public sealed class DemoDashboardQueries : IDashboardQueries
{
    public static readonly Guid CompetitionId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid SeasonId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Home = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Away = Guid.Parse("00000000-0000-0000-0000-000000000004");
    public Task<DashboardPage<DashboardFixture>> FixturesAsync(DashboardFilter f, CancellationToken token = default)
    {
        f.Validate(); token.ThrowIfCancellationRequested();
        var rows = Enumerable.Range(1, 6).Select(i => new DashboardFixture(new Guid(i, 0, 0, new byte[8]), Guid.Empty,
            "DEMO", CompetitionId, SeasonId, Home, Away, new DateOnly(2026, 10, i),
            new[] { "Scheduled", "Postponed", "Completed", "Unknown", "Conflict", "Cancelled" }[i - 1],
            i == 3 ? 2 : null, i == 3 ? 1 : null, new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), 0, 0, "DEMO"))
            .Where(r => (f.Competition is null || f.Competition == r.CompetitionId) && (f.Season is null || f.Season == r.SeasonId) &&
                (f.Team is null || f.Team == r.HomeId || f.Team == r.AwayId) && (f.From is null || r.Date >= f.From) &&
                (f.To is null || r.Date <= f.To) && (f.Status is null || f.Status == r.Status)).ToArray();
        return Task.FromResult(new DashboardPage<DashboardFixture>(rows.Skip(f.Offset).Take(f.Limit).ToArray(), f.Offset, f.Limit, rows.Length > f.Offset + f.Limit, true));
    }
    public Task<DashboardPage<DashboardChoice>> ChoicesAsync(string kind, DashboardFilter f, CancellationToken token = default)
    {
        f.Validate(); token.ThrowIfCancellationRequested();
        DashboardChoice[] choices = kind switch
        {
            "competitions" => [new(CompetitionId, "DEMO · Fictional League")],
            "seasons" when f.Competition is null || f.Competition == CompetitionId => [new(SeasonId, "DEMO · 2026", CompetitionId)],
            "teams" when (f.Competition is null || f.Competition == CompetitionId) && (f.Season is null || f.Season == SeasonId) => [new(Home, "DEMO · Amber Comets"), new(Away, "DEMO · Cobalt Owls")],
            _ => []
        };
        return Task.FromResult(new DashboardPage<DashboardChoice>(choices.Skip(f.Offset).Take(f.Limit).ToArray(), f.Offset, f.Limit, choices.Length > f.Offset + f.Limit, true));
    }
    public Task<DashboardPage<DashboardBacktest>> BacktestsAsync(DashboardFilter f, CancellationToken token = default)
    { f.Validate(); return Task.FromResult(new DashboardPage<DashboardBacktest>([], f.Offset, f.Limit, false, true)); }
    public async Task<DashboardQuality> QualityAsync(DashboardFilter f, CancellationToken token = default)
    { var rows = await FixturesAsync(f with { Offset = 0, Limit = 100 }, token); return new("DEMO", false, rows.Items.Count, 0, rows.Items.Count, rows.Items.Count, 1, 1, "DEMO", ["presentation_only", "no_observed_accuracy"]); }
}
