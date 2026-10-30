using System.Security.Cryptography;
using System.Text;
using BetStats.Application.Football;
using BetStats.Application.Ingestion;
using BetStats.Application.Quality;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Ingestion;

/// <summary>Operator request for a one-command authorized import of one league season.</summary>
public sealed record FootballHistoryImportRequest(
    string PayloadPath,
    string WorkDirectory,
    FootballImportScope Scope,
    string SourceCode,
    string SourceName,
    string TermsReference,
    string CompetitionName,
    string? CountryCode,
    string SeasonName,
    string OperatorId,
    string Reason);

public sealed record FootballHistoryPreparation(
    Guid SourceId,
    Guid CompetitionId,
    Guid SeasonId,
    bool CreatedSource,
    int CreatedTeams,
    int CreatedEvents,
    int ExistingEvents);

public sealed record FootballHistoryImportResult(
    int Version,
    string ProjectedPath,
    int InputRows,
    int ProjectedRows,
    int OtherCompetitionRows,
    IReadOnlyList<ImportIssue> ProjectionIssues,
    FootballHistoryPreparation Preparation,
    Guid OperationId,
    ImportReport Import);

/// <summary>
/// BS-020: one explicit, approved operator command that imports a real league season through the
/// existing BS-013 path. It projects the export onto football-history-v1, records the operator's
/// source permission and reviewed identities, then runs the existing plan and capture operations.
/// </summary>
public sealed class FootballHistoryImport(BetStatsDbContext db, FootballImportOperations operations)
{
    private static readonly DataPurpose[] AllowedPurposes =
    [
        DataPurpose.DataRetrieval,
        DataPurpose.RawPayloadStorage,
        DataPurpose.HistoricalRetention,
        DataPurpose.InternalAnalytics,
        DataPurpose.ModelTraining,
    ];

    public async Task<FootballHistoryImportResult> ImportAsync(FootballHistoryImportRequest request, CancellationToken token = default)
    {
        Validate(request);
        var original = await File.ReadAllBytesAsync(request.PayloadPath, token);
        var projection = FootballHistoryProjection.Project(original, request.Scope);
        if (projection.ProjectedRows == 0)
        {
            throw new InvalidDataException("no_rows_for_competition");
        }

        var projectedPath = await WriteProjectionAsync(request, projection.Content, token);
        var parsed = new HistoricalFootballCsvParser().Parse(projection.Content, request.Scope, token);
        if (parsed.Records.Count == 0)
        {
            throw new InvalidDataException("no_valid_rows");
        }

        var preparation = await PrepareAsync(request, parsed.Records, token);
        var plan = await operations.PlanAsync(preparation.SourceId, projectedPath, request.Scope, token);
        var operationId = OperationId(plan);
        var previous = await operations.InspectAsync(operationId, token);
        var recover = previous is not null && previous.Status != ResultOperationStatus.Succeeded;
        var report = await operations.RunAsync(operationId, plan, projectedPath, request.OperatorId, request.Reason, recover, token);

        return new(1, projectedPath, projection.InputRows, projection.ProjectedRows, projection.OtherCompetitionRows,
            projection.Issues, preparation, operationId, report);
    }

    /// <summary>
    /// Records the operator's source permission and reviewed identities for every competition, season,
    /// team and match in the parsed rows. Existing reviewed identities are reused, so the step is idempotent.
    /// </summary>
    public async Task<FootballHistoryPreparation> PrepareAsync(FootballHistoryImportRequest request,
        IReadOnlyList<FootballMatchRecord> records, CancellationToken token = default)
    {
        Validate(request);
        var decidedBy = "operator:" + request.OperatorId;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var now = await QualityPersistence.Now(db, token);

        var (sourceId, createdSource) = await EnsureSourceAsync(request, decidedBy, now, token);
        var reviewer = new IdentityReviewer(db, sourceId, decidedBy, request.Reason, now);
        var division = request.Scope.CompetitionReference;

        var competition = await EnsureCompetitionAsync(reviewer, request, division, token);
        var season = await EnsureSeasonAsync(reviewer, request, competition, division, token);
        var (teams, createdTeams) = await EnsureTeamsAsync(reviewer, records, token);

        var createdEvents = 0;
        var existingEvents = 0;
        foreach (var record in records)
        {
            if (await reviewer.ReviewedTargetAsync(CanonicalEntityKind.SportingEvent, record.MatchReference, token) is not null)
            {
                existingEvents++;
                continue;
            }

            var status = record.Status ?? SportingEventStatus.Scheduled;
            var sportingEvent = new SportingEvent(Guid.NewGuid(), competition, season, null, status, now);
            sportingEvent.AddParticipant(teams[record.HomeReference], ParticipantRole.Home, 1);
            sportingEvent.AddParticipant(teams[record.AwayReference], ParticipantRole.Away, 2);
            db.Add(sportingEvent);
            await reviewer.ReviewAsync(CanonicalEntityKind.SportingEvent, record.MatchReference, sportingEvent.Id, token);
            createdEvents++;
        }

        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        db.ChangeTracker.Clear();
        return new(sourceId, competition.Id, season.Id, createdSource, createdTeams, createdEvents, existingEvents);
    }

    private static void Validate(FootballHistoryImportRequest request)
    {
        QualityPersistence.Operator(request.OperatorId, request.Reason);
        if (request.OperatorId.Length > 190)
        {
            throw new ArgumentException("Operator identifier must be at most 190 characters.");
        }

        if (!request.Scope.IsValid)
        {
            throw new ArgumentException("Explicit competition and season are required.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceCode) || string.IsNullOrWhiteSpace(request.CompetitionName))
        {
            throw new ArgumentException("Source code and competition name are required.");
        }

        _ = FootballHistoryProjection.SeasonStartYear(request.Scope.SeasonReference);
    }

    private static async Task<string> WriteProjectionAsync(FootballHistoryImportRequest request, byte[] content,
        CancellationToken token)
    {
        Directory.CreateDirectory(request.WorkDirectory);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content))[..12];
        var name = $"{Safe(request.Scope.CompetitionReference)}-{Safe(request.Scope.SeasonReference)}-{hash}.csv";
        var path = Path.Combine(Path.GetFullPath(request.WorkDirectory), name);
        if (!File.Exists(path))
        {
            await File.WriteAllBytesAsync(path, content, token);
        }

        return path;

        static string Safe(string value) => new(value.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
    }

    private static Guid OperationId(FootballImportPlan plan)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("bs-020:" + plan.Fingerprint));
        return new Guid(hash.AsSpan(0, 16));
    }

    private async Task<(Guid Id, bool Created)> EnsureSourceAsync(FootballHistoryImportRequest request, string decidedBy,
        DateTime now, CancellationToken token)
    {
        var existing = await db.DataSources.SingleOrDefaultAsync(s => s.Code == request.SourceCode, token);
        if (existing is not null)
        {
            return (existing.Id, false);
        }

        if (string.IsNullOrWhiteSpace(request.TermsReference))
        {
            throw new ArgumentException("A terms reference describing the operator's permission is required for a new source.");
        }

        var source = new DataSource
        {
            Id = Guid.NewGuid(),
            Code = request.SourceCode,
            DisplayName = request.SourceName,
            IsEnabled = true,
            CreatedAtUtc = now,
        };
        var permissions = AllowedPurposes.Select(p => new PurposePermission(p, PermissionDecision.Allowed));
        var policy = new SourcePolicy(Guid.NewGuid(), source.Id, 1, now, null, request.TermsReference,
            decidedBy + ": " + request.Reason, now, permissions);
        db.AddRange(source, policy);
        await db.SaveChangesAsync(token);

        policy.Approve(Guid.NewGuid(), decidedBy, "Operator-approved local, non-commercial analytical use", now, now);
        await db.SaveChangesAsync(token);
        return (source.Id, true);
    }

    private async Task<Competition> EnsureCompetitionAsync(IdentityReviewer reviewer, FootballHistoryImportRequest request,
        string division, CancellationToken token)
    {
        var reference = "provider:competition:" + division;
        if (await reviewer.ReviewedTargetAsync(CanonicalEntityKind.Competition, reference, token) is { } id)
        {
            return await db.Competitions.SingleAsync(c => c.Id == id, token);
        }

        var competition = new Competition(Guid.NewGuid(), FootballQualityRules.Football, request.CompetitionName,
            request.CountryCode, CompetitionType.League);
        db.Add(competition);
        await reviewer.ReviewAsync(CanonicalEntityKind.Competition, reference, competition.Id, token);
        return competition;
    }

    private async Task<Season> EnsureSeasonAsync(IdentityReviewer reviewer, FootballHistoryImportRequest request,
        Competition competition, string division, CancellationToken token)
    {
        var reference = FootballDataCsvParser.SeasonReference(division, request.Scope.SeasonReference);
        if (await reviewer.ReviewedTargetAsync(CanonicalEntityKind.Season, reference, token) is { } id)
        {
            var existing = await db.Seasons.SingleAsync(s => s.Id == id, token);
            if (existing.CompetitionId != competition.Id)
            {
                throw new InvalidOperationException("Reviewed season belongs to another competition.");
            }

            return existing;
        }

        var season = new Season(Guid.NewGuid(), competition.Id, request.SeasonName);
        db.Add(season);
        await reviewer.ReviewAsync(CanonicalEntityKind.Season, reference, season.Id, token);
        return season;
    }

    private async Task<(Dictionary<string, Participant> Teams, int Created)> EnsureTeamsAsync(IdentityReviewer reviewer,
        IReadOnlyList<FootballMatchRecord> records, CancellationToken token)
    {
        var names = records
            .SelectMany(r => new (string Reference, string Name)[] { (r.HomeReference, r.HomeName), (r.AwayReference, r.AwayName) })
            .DistinctBy(t => t.Reference)
            .ToArray();
        var teams = new Dictionary<string, Participant>(StringComparer.Ordinal);
        var created = 0;

        foreach (var (reference, name) in names)
        {
            if (await reviewer.ReviewedTargetAsync(CanonicalEntityKind.Participant, reference, token) is { } id)
            {
                teams[reference] = await db.Participants.SingleAsync(p => p.Id == id, token);
                continue;
            }

            var team = new Participant(Guid.NewGuid(), FootballQualityRules.Football, name, ParticipantType.Team);
            db.Add(team);
            await reviewer.ReviewAsync(CanonicalEntityKind.Participant, reference, team.Id, token);
            teams[reference] = team;
            created++;
        }

        return (teams, created);
    }

    /// <summary>Reads and records operator identity decisions for one source.</summary>
    private sealed class IdentityReviewer(BetStatsDbContext db, Guid source, string decidedBy, string reason, DateTime now)
    {
        public async Task<Guid?> ReviewedTargetAsync(CanonicalEntityKind kind, string reference, CancellationToken token)
        {
            var identity = await QualityPersistence.Anchor(db, source, kind, reference, token);
            if (identity is null)
            {
                return null;
            }

            var decision = await QualityPersistence.Decision(db, identity.Id, now, token);
            var reviewed = decision is { Status: ResolutionStatus.Resolved }
                && !decision.DecidedBy.StartsWith("ingestion:", StringComparison.Ordinal);
            return reviewed ? Target(decision!) : null;
        }

        public async Task ReviewAsync(CanonicalEntityKind kind, string reference, Guid target, CancellationToken token)
        {
            var identity = await QualityPersistence.Anchor(db, source, kind, reference, token);
            IdentityResolution? previous = null;
            if (identity is null)
            {
                identity = new ProviderIdentity(Guid.NewGuid(), source, kind, reference, now);
                db.Add(identity);
            }
            else
            {
                previous = await QualityPersistence.Decision(db, identity.Id, now, token);
            }

            var decision = new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Resolved,
                new CanonicalReference(kind, target), decidedBy, reason, now, previous);
            db.Add(decision);
        }

        private static Guid? Target(IdentityResolution decision) =>
            decision.CanonicalCompetitionId
            ?? decision.CanonicalSeasonId
            ?? decision.CanonicalParticipantId
            ?? decision.CanonicalSportingEventId;
    }
}
