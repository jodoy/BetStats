using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;

namespace BetStats.Application.Models;

public enum FootballModelKind { Elo, Poisson, DixonColes }
public sealed record EloParameters(decimal BaseRating = 1500, decimal HomeAdvantage = 60, decimal K = 20,
    decimal Scale = 400, decimal SeasonRetention = 1, decimal GoalBridgeScale = 800);
public sealed record GoalParameters(decimal PriorGoals = 1.25m, decimal PriorMatches = 2,
    decimal HomeMultiplier = 1.1m, decimal MaximumRate = 6, int MaximumGoals = 30,
    decimal MaximumDiscardedMass = 0.000001m, decimal Rho = 0, decimal HalfPriorGoals = 0.55m);
public sealed record FootballModelDefinition(int Version, FootballModelKind Kind, EloParameters Elo, GoalParameters Goals,
    int MinimumMatches = 3, int MinimumHalfMatches = 3, int HistoryDays = 30)
{
    public string Predictor => Kind switch { FootballModelKind.Elo => "football-elo", FootballModelKind.Poisson => "football-poisson", FootballModelKind.DixonColes => "football-dixon-coles", _ => throw new ArgumentException("Unknown model.") };
    public void Validate()
    {
        if (Version != 1 || !Enum.IsDefined(Kind) || Elo is null || Goals is null || MinimumMatches is < 1 or > 1000 || MinimumHalfMatches is < 1 or > 1000 || HistoryDays != 30 ||
            Elo.BaseRating is < 0 or > 10000 || Elo.HomeAdvantage is < -500 or > 500 || Elo.K is <= 0 or > 100 || Elo.Scale is < 100 or > 1000 || Elo.SeasonRetention is < 0 or > 1 || Elo.GoalBridgeScale is < 400 or > 2000 ||
            Goals.PriorGoals is <= 0 or > 5 || Goals.HalfPriorGoals is <= 0 or > 5 || Goals.PriorMatches is <= 0 or > 100 || Goals.HomeMultiplier is <= 0 or > 3 || Goals.MaximumRate is <= 0 or > 10 || Goals.MaximumGoals is < 3 or > 60 ||
            Goals.MaximumDiscardedMass is <= 0 or > 0.001m || Goals.Rho is < -1 or > 1 || Kind != FootballModelKind.DixonColes && Goals.Rho != 0)
            throw new ArgumentException("Explicit bounded model v1 parameters required.");
    }
}
// Provider-neutral, reviewed canonical history only; original evidence remains in the dataset.
public sealed record ModelMatch(Guid EventId, Guid HomeId, Guid AwayId, Guid SeasonId, DateOnly EventDate,
    int HomeGoals, int AwayGoals, int? HalfHomeGoals, int? HalfAwayGoals, Guid ObservationId, int ObservationVersion,
    DateTime AvailableAtUtc, DateTime RecordedAtUtc, IReadOnlyList<Guid> EvidenceIds);
public sealed record ModelHistory(int Version, Guid SeasonId, IReadOnlyList<ModelMatch> Matches);
public sealed record ExactScore(int Home, int Away, decimal Probability);
public sealed record GoalTotal(int Goals, decimal Probability);
public sealed record ScoreDistribution(int Version, decimal HomeRate, decimal AwayRate, decimal DiscardedMass,
    IReadOnlyList<ExactScore> Scores, IReadOnlyList<GoalTotal> Totals)
{
    public PredictionValue Predict(EvaluationTarget target) => target switch
    {
        EvaluationTarget.MatchWinner => new([Scores.Where(s => s.Home > s.Away).Sum(s => s.Probability), Scores.Where(s => s.Home == s.Away).Sum(s => s.Probability), Scores.Where(s => s.Home < s.Away).Sum(s => s.Probability)], null),
        EvaluationTarget.BothTeamsScoring => Binary(Scores.Where(s => s.Home > 0 && s.Away > 0).Sum(s => s.Probability)),
        EvaluationTarget.OverUnder25 => Binary(Totals.Where(s => s.Goals > 2).Sum(s => s.Probability)),
        EvaluationTarget.TotalGoals => new([], Totals.Sum(s => s.Goals * s.Probability)),
        EvaluationTarget.FirstHalfGoalOccurrence => Binary(1 - Scores.Single(s => s.Home == 0 && s.Away == 0).Probability),
        EvaluationTarget.FirstHalfTotalGoals => new([], Totals.Sum(s => s.Goals * s.Probability)),
        _ => throw new ArgumentException("Unsupported market.")
    };
    private static PredictionValue Binary(decimal yes) => new([1 - yes, yes], null);
}
public sealed record ModelProvenance(int Version, FootballModelDefinition Definition, string DefinitionHash, string InputHash,
    decimal HomeRating, decimal AwayRating, int HomeMatches, int AwayMatches, int HomeHalfMatches, int AwayHalfMatches,
    bool Warmed, bool HalfWarmed, ScoreDistribution FullTime, ScoreDistribution HalfTime);
public sealed record ModelForecast(Guid EventId, DateTime CutoffUtc, ModelProvenance Provenance);
public interface IHistoricalFootballPredictionProvider : IHistoricalPredictionProvider
{
    FootballModelDefinition Definition { get; }
    ModelProvenance Simulate(PredictionInput input);
}
public sealed record PredictionModelProvenance(int Version, FootballModelDefinition Definition, string DefinitionHash, string InputHash,
    decimal HomeRating, decimal AwayRating, int HomeMatches, int AwayMatches, int HomeHalfMatches, int AwayHalfMatches,
    bool Warmed, bool HalfWarmed, string FullScoreHash, string HalfScoreHash)
{
    public static PredictionModelProvenance From(ModelProvenance p) => new(p.Version, p.Definition, p.DefinitionHash, p.InputHash, p.HomeRating, p.AwayRating,
        p.HomeMatches, p.AwayMatches, p.HomeHalfMatches, p.AwayHalfMatches, p.Warmed, p.HalfWarmed, CanonicalDatasetJson.Fingerprint(p.FullTime), CanonicalDatasetJson.Fingerprint(p.HalfTime));
}

public static class FootballModelInputs
{
    public static PredictionInput From(FootballResultDatasetRow row, PredictionInput validated, Guid season)
    {
        var day = DateOnly.FromDateTime(validated.CutoffUtc);
        var candidates = row.FeatureEvidence.Results.Where(e => e.Eligible && FootballResultRules.LabelEligible(e.Observation.Value) &&
            e.Observation.EventId != validated.EventId && e.Observation.EventDate < day && e.Observation.EventDate < row.Metadata.Target.EventDate && e.Observation.EventDate >= day.AddDays(-30)).ToArray();
        if (candidates.GroupBy(e => e.Observation.EventId).Any(g => g.Count() != 1)) throw new InvalidDataException("Conflicting model history.");
        var history = candidates.Select(e => new ModelMatch(e.Observation.EventId, e.Observation.HomeId, e.Observation.AwayId, e.Observation.SeasonId, e.Observation.EventDate,
            e.Observation.Value.FullTime.Home!.Value, e.Observation.Value.FullTime.Away!.Value,
            e.Observation.Value.HalfTime.Home, e.Observation.Value.HalfTime.Away, e.Observation.Id, e.Observation.Version,
            e.Observation.AvailableAtUtc, e.Observation.RecordedAtUtc, e.IdentityDecisionIds.Concat(e.QualityIds).Append(e.Observation.RawId).Order().ToArray())).OrderBy(m => m.EventDate).ThenBy(m => m.EventId).ToArray();
        var result = validated with { Version = 2, History = new(1, season, history) }; Validate(result); return result;
    }
    public static void Validate(PredictionInput input)
    {
        if (input.Version != 2 || !BacktestRules.Utc(input.CutoffUtc) || !BacktestRules.Hash(input.FeatureHash) || input.EventId == Guid.Empty || input.HomeId == Guid.Empty || input.AwayId == Guid.Empty || input.HomeId == input.AwayId ||
            input.Features.SchemaVersion != 3 || input.Features.TargetEventId != input.EventId || input.Features.PredictionCutoffUtc != input.CutoffUtc || input.History is not { Version: 1 } h || h.SeasonId == Guid.Empty || h.Matches.Count > 1000)
            throw new InvalidDataException("Canonical model input v2 required.");
        var day = DateOnly.FromDateTime(input.CutoffUtc);
        foreach (var m in h.Matches)
            if (m.EventId == Guid.Empty || m.EventId == input.EventId || m.HomeId == Guid.Empty || m.AwayId == Guid.Empty || m.HomeId == m.AwayId || m.SeasonId == Guid.Empty || m.ObservationId == Guid.Empty || m.ObservationVersion < 1 || m.EvidenceIds.Count == 0 || m.EvidenceIds.Any(id => id == Guid.Empty) ||
                m.EventDate >= day || m.EventDate < day.AddDays(-30) || !BacktestRules.Utc(m.AvailableAtUtc) || !BacktestRules.Utc(m.RecordedAtUtc) || m.AvailableAtUtc > input.CutoffUtc || m.RecordedAtUtc > input.CutoffUtc ||
                m.HomeGoals < 0 || m.AwayGoals < 0 || (long)m.HomeGoals + m.AwayGoals > int.MaxValue || (m.HalfHomeGoals is null) != (m.HalfAwayGoals is null) || m.HalfHomeGoals < 0 || m.HalfAwayGoals < 0 || m.HalfHomeGoals > m.HomeGoals || m.HalfAwayGoals > m.AwayGoals)
                throw new InvalidDataException("Invalid or future canonical model history.");
        if (h.Matches.GroupBy(m => m.EventId).Any(g => g.Count() != 1)) throw new InvalidDataException("Ambiguous/corrected duplicate history; replay requires one reviewed as-of version.");
    }
}

public static class PoissonScores
{
    public static ScoreDistribution Build(decimal home, decimal away, GoalParameters p, bool corrected)
    {
        new FootballModelDefinition(1, corrected ? FootballModelKind.DixonColes : FootballModelKind.Poisson, new(), p).Validate();
        if (home <= 0 || away <= 0 || home > p.MaximumRate || away > p.MaximumRate) throw new ArgumentException("Rates must be positive and bounded, never silently clipped.");
        var h = Terms(home, p.MaximumGoals); var a = Terms(away, p.MaximumGoals);
        var discarded = 1 - h.Sum() * a.Sum();
        if (discarded < -0.000000000001 || discarded > (double)p.MaximumDiscardedMass) throw new InvalidDataException("Truncated probability mass exceeds the model contract.");
        var rho = corrected ? (double)p.Rho : 0;
        var factors = new[] { 1 - (double)(home * away) * rho, 1 + (double)home * rho, 1 + (double)away * rho, 1 - rho };
        if (factors.Any(x => !double.IsFinite(x) || x <= 0)) throw new ArgumentException("Dixon-Coles correction must have four strictly positive factors.");
        var cells = new List<(int H, int A, double P)>();
        for (var i = 0; i <= p.MaximumGoals; i++)
            for (var j = 0; j <= p.MaximumGoals; j++)
            {
                var tau = (i, j) switch { (0, 0) => factors[0], (0, 1) => factors[1], (1, 0) => factors[2], (1, 1) => factors[3], _ => 1 };
                cells.Add((i, j, h[i] * a[j] * tau));
            }
        var mass = cells.Sum(c => c.P);
        if (!double.IsFinite(mass) || mass <= 0) throw new InvalidDataException("Invalid score mass.");
        var scores = cells.Select(c => new ExactScore(c.H, c.A, decimal.Round((decimal)(c.P / mass), 15, MidpointRounding.ToEven))).ToArray();
        var largest = Enumerable.Range(0, scores.Length).OrderByDescending(i => scores[i].Probability).First();
        scores[largest] = scores[largest] with { Probability = scores[largest].Probability + 1 - scores.Sum(c => c.Probability) };
        if (scores.Any(s => s.Probability < 0 || s.Probability > 1) || scores.Sum(s => s.Probability) != 1) throw new InvalidDataException("Quantized mass validation failed.");
        return new(1, home, away, decimal.Round((decimal)Math.Max(0, discarded), 15), scores,
            scores.GroupBy(s => s.Home + s.Away).OrderBy(g => g.Key).Select(g => new GoalTotal(g.Key, g.Sum(s => s.Probability))).ToArray());
    }
    private static double[] Terms(decimal rate, int maximum)
    {
        var result = new double[maximum + 1]; result[0] = Math.Exp(-(double)rate);
        for (var i = 1; i <= maximum; i++) result[i] = result[i - 1] * (double)rate / i;
        return result;
    }
}

public sealed class FootballPredictor(FootballModelDefinition definition) : IHistoricalFootballPredictionProvider
{
    public FootballModelDefinition Definition => definition;
    public string Name => definition.Predictor;
    public int Version => definition.Version;
    public PredictionValue Predict(PredictionInput input, EvaluationTarget target)
    {
        var p = Simulate(input); return (target is EvaluationTarget.FirstHalfGoalOccurrence or EvaluationTarget.FirstHalfTotalGoals ? p.HalfTime : p.FullTime).Predict(target);
    }
    public ModelProvenance Simulate(PredictionInput input)
    {
        definition.Validate(); FootballModelInputs.Validate(input); var history = input.History!;
        var ratings = new Dictionary<Guid, decimal>(); var seasons = new Dictionary<Guid, Guid>();
        decimal Rating(Guid id) => ratings.GetValueOrDefault(id, definition.Elo.BaseRating);
        // Same-day games update from the pre-day state; input enumeration never breaks ties.
        foreach (var day in history.Matches.GroupBy(m => m.EventDate).OrderBy(g => g.Key))
        {
            foreach (var m in day.OrderBy(m => m.EventId))
                foreach (var id in new[] { m.HomeId, m.AwayId })
                {
                    if (seasons.TryGetValue(id, out var previous) && previous != m.SeasonId) ratings[id] = definition.Elo.BaseRating + (Rating(id) - definition.Elo.BaseRating) * definition.Elo.SeasonRetention;
                    seasons[id] = m.SeasonId;
                }
            if (day.SelectMany(m => new[] { (m.HomeId, m.SeasonId), (m.AwayId, m.SeasonId) }).GroupBy(x => x.Item1).Any(g => g.Select(x => x.SeasonId).Distinct().Count() > 1)) throw new InvalidDataException("Ambiguous same-day season boundary.");
            var changes = new Dictionary<Guid, decimal>();
            foreach (var m in day.OrderBy(m => m.EventId))
            {
                var expected = Expected(Rating(m.HomeId) + definition.Elo.HomeAdvantage, Rating(m.AwayId), definition.Elo.Scale);
                var actual = m.HomeGoals > m.AwayGoals ? 1m : m.HomeGoals == m.AwayGoals ? .5m : 0m;
                var delta = decimal.Round(definition.Elo.K * (actual - expected), 12);
                changes[m.HomeId] = changes.GetValueOrDefault(m.HomeId) + delta; changes[m.AwayId] = changes.GetValueOrDefault(m.AwayId) - delta;
            }
            foreach (var pair in changes) ratings[pair.Key] = Rating(pair.Key) + pair.Value;
        }
        decimal Current(Guid id) => seasons.TryGetValue(id, out var old) && old != history.SeasonId ? definition.Elo.BaseRating + (Rating(id) - definition.Elo.BaseRating) * definition.Elo.SeasonRetention : Rating(id);
        var hm = history.Matches.Where(m => m.HomeId == input.HomeId || m.AwayId == input.HomeId).ToArray();
        var am = history.Matches.Where(m => m.HomeId == input.AwayId || m.AwayId == input.AwayId).ToArray();
        var hh = hm.Where(m => m.HalfHomeGoals is not null).ToArray(); var ah = am.Where(m => m.HalfHomeGoals is not null).ToArray();
        decimal Mean(ModelMatch[] matches, Guid team, bool attack, bool half)
        {
            long sum = matches.Sum(m => (long)(half ? (m.HomeId == team) == attack ? m.HalfHomeGoals!.Value : m.HalfAwayGoals!.Value : (m.HomeId == team) == attack ? m.HomeGoals : m.AwayGoals));
            var prior = half ? definition.Goals.HalfPriorGoals : definition.Goals.PriorGoals;
            return (sum + prior * definition.Goals.PriorMatches) / (matches.Length + definition.Goals.PriorMatches);
        }
        ScoreDistribution Grid(bool half)
        {
            var home = (Mean(half ? hh : hm, input.HomeId, true, half) + Mean(half ? ah : am, input.AwayId, false, half)) / 2 * definition.Goals.HomeMultiplier;
            var away = (Mean(half ? ah : am, input.AwayId, true, half) + Mean(half ? hh : hm, input.HomeId, false, half)) / 2;
            if (definition.Kind == FootballModelKind.Elo)
            {
                var gap = Current(input.HomeId) + definition.Elo.HomeAdvantage - Current(input.AwayId);
                var prior = half ? definition.Goals.HalfPriorGoals : definition.Goals.PriorGoals;
                var bridge = Math.Pow(10, (double)(gap / definition.Elo.GoalBridgeScale));
                if (!double.IsFinite(bridge) || bridge <= 0 || bridge > (double)(definition.Goals.MaximumRate / prior) || bridge < (double)(prior / definition.Goals.MaximumRate))
                    throw new ArgumentException("Elo goal bridge exceeds declared rate bounds; no clipping.");
                var multiplier = (decimal)bridge;
                home = prior * multiplier; away = prior / multiplier;
            }
            return PoissonScores.Build(home, away, definition.Goals, definition.Kind == FootballModelKind.DixonColes);
        }
        return new(1, definition, CanonicalDatasetJson.Fingerprint(definition), CanonicalDatasetJson.Fingerprint(input), Current(input.HomeId), Current(input.AwayId), hm.Length, am.Length, hh.Length, ah.Length,
            hm.Length >= definition.MinimumMatches && am.Length >= definition.MinimumMatches, hh.Length >= definition.MinimumHalfMatches && ah.Length >= definition.MinimumHalfMatches, Grid(false), Grid(true));
    }
    public static decimal Expected(decimal home, decimal away, decimal scale)
    {
        if (scale <= 0) throw new ArgumentException("Positive Elo scale required.");
        return decimal.Round((decimal)(1 / (1 + Math.Pow(10, (double)((away - home) / scale)))), 15);
    }
}
