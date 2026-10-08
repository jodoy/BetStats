using BetStats.Domain.Coverage;
using BetStats.Domain.Observations;

namespace BetStats.Application.Coverage;

public static class CoverageRules
{
    public static IReadOnlyList<CoverageInterval> Gaps(CoverageInterval requested, IEnumerable<CoverageInterval> supplied)
    {
        requested.Validate(); var intervals = supplied.ToArray();
        if (intervals.Length > 200) throw new ArgumentException("Interval bound exceeded.");
        foreach (var interval in intervals) interval.Validate();
        if (intervals.Any(i => !requested.Compatible(i))) throw new ArgumentException("Interval bases cannot be mixed.");
        var cursor = requested.Start; var gaps = new List<CoverageInterval>();
        foreach (var i in intervals.OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            var start = Math.Max(requested.Start, i.Start); var end = Math.Min(requested.End, i.End);
            if (end <= start || end <= cursor) continue;
            if (start > cursor) gaps.Add(requested.Slice(cursor, start)); cursor = Math.Max(cursor, end);
        }
        if (cursor < requested.End) gaps.Add(requested.Slice(cursor, requested.End));
        return gaps;
    }
    public static CoverageInterval? Intersection(CoverageInterval a, CoverageInterval b)
    {
        a.Validate(); b.Validate(); if (!a.Compatible(b)) throw new ArgumentException("Unknown or incompatible time basis.");
        var start = Math.Max(a.Start, b.Start); var end = Math.Min(a.End, b.End); return start < end ? a.Slice(start, end) : null;
    }
    public static CoverageStatus Classify(CoverageInterval requested, IReadOnlyList<CoverageItem> items)
    {
        if (items.Count > 200) throw new ArgumentException("Coverage bound exceeded.");
        var strong = items.Where(i => i.Status is CoverageStatus.VerifiedComplete or CoverageStatus.VerifiedEmpty).ToArray();
        for (var i = 0; i < strong.Length; i++) for (var j = i + 1; j < strong.Length; j++)
            if (Intersection(strong[i].Evidence.Scope.Interval, strong[j].Evidence.Scope.Interval) is not null &&
                (strong[i].Status != strong[j].Status || !strong[i].Evidence.SupportingObservationIds.Order().SequenceEqual(strong[j].Evidence.SupportingObservationIds.Order()))) return CoverageStatus.Conflicting;
        if (items.Any(i => i.Status == CoverageStatus.Conflicting)) return CoverageStatus.Conflicting;
        if (strong.Length > 0 && Gaps(requested, strong.Select(i => i.Evidence.Scope.Interval)).Count == 0)
            return strong.All(i => i.Status == CoverageStatus.VerifiedEmpty) ? CoverageStatus.VerifiedEmpty : CoverageStatus.VerifiedComplete;
        if (items.Any(i => i.Status is CoverageStatus.Partial or CoverageStatus.VerifiedComplete or CoverageStatus.VerifiedEmpty)) return CoverageStatus.Partial;
        return items.Count > 0 && items.All(i => i.Status == CoverageStatus.Expired) ? CoverageStatus.Expired : CoverageStatus.Unknown;
    }
    public static FeatureCoverageDecision Gate(FeatureCoverageRequirement requirement, IReadOnlyList<CoverageReport> reports)
    {
        if (string.IsNullOrWhiteSpace(requirement.FeatureName) || requirement.Version != 1 || requirement.MinimumQualityVersion != 1 || requirement.ObservationTypes.Count == 0 ||
            requirement.LookbackDays is < 1 or > 730 || requirement.RequiredStatus != "Completed" || reports.Count is < 1 or > 10 ||
            requirement.PartialAllowed == requirement.CompletenessRequired) throw new ArgumentException("Explicit supported feature coverage requirement required.");
        foreach (var report in reports)
        {
            report.Query.Scope.Validate();
            if (requirement.ParticipantRequired && report.Query.Scope.ParticipantId is null ||
                requirement.LookbackDays is { } days && (report.Query.Scope.Interval.Kind != IntervalKind.Calendar || report.Query.Scope.Interval.End - report.Query.Scope.Interval.Start != days))
                throw new ArgumentException("Coverage report does not satisfy declared participant/window dimensions.");
            var first = reports[0].Query;
            if (report.Query.AsOfUtc != first.AsOfUtc || report.Query.Mode != first.Mode || report.Query.ReconstructionUtc != first.ReconstructionUtc ||
                report.Query.Purpose != first.Purpose || report.Query.Context != first.Context ||
                !(report.Query.Scope with { ObservationType = first.Scope.ObservationType }).SameDimensions(first.Scope) || report.Query.Scope.Interval != first.Scope.Interval)
                throw new ArgumentException("Coverage reports must share one explicit evidence scope and knowledge boundary.");
        }
        FeatureCoverageOutcome outcome; var reasons = new List<string>();
        if (reports.Any(r => !r.Authorized)) outcome = FeatureCoverageOutcome.Unauthorized;
        else if (reports.Any(r => r.Status == CoverageStatus.Conflicting)) outcome = FeatureCoverageOutcome.ConflictingCoverage;
        else if (reports.Any(r => r.Status == CoverageStatus.Expired)) outcome = FeatureCoverageOutcome.ExpiredCoverage;
        else if (requirement.ObservationTypes.Any(t => !reports.Any(r => r.Query.Scope.ObservationType == t))) outcome = FeatureCoverageOutcome.UnknownCoverage;
        else if (reports.All(r => r.Status is CoverageStatus.VerifiedComplete or CoverageStatus.VerifiedEmpty)) outcome = requirement.PartialAllowed ? FeatureCoverageOutcome.EligibleWithPartialCoverage : FeatureCoverageOutcome.Eligible;
        else if (requirement.PartialAllowed) { outcome = FeatureCoverageOutcome.EligibleWithPartialCoverage; reasons.Add("observed_history_only_no_completeness_claim"); }
        else outcome = reports.Any(r => r.Status == CoverageStatus.Unknown) ? FeatureCoverageOutcome.UnknownCoverage : FeatureCoverageOutcome.InsufficientCoverage;
        return new(requirement.FeatureName, outcome, reports.SelectMany(r => r.Items).Select(i => i.Evidence.Id).Distinct().Order().ToArray(),
            reports.SelectMany(r => r.Items).Where(i => i.Review is not null).Select(i => i.Review!.Id).Distinct().Order().ToArray(), reasons);
    }
}
