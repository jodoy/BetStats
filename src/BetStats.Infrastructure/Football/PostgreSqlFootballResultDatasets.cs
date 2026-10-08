using System.Data;
using BetStats.Application.Datasets;
using BetStats.Application.Football;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Football;

public sealed class PostgreSqlFootballResultDatasets(BetStatsDbContext db, IDatasets metadata, IFootballResults results) : IFootballResultDatasets
{
    private static IEnumerable<FootballResultEvidence> Evidence(FootballResultManifest m) => m.Rows.SelectMany(r => r.FeatureEvidence.Results.Concat(r.LabelEvidence.Results));
    private static string FeatureHash(FootballResultDatasetRow row) => CanonicalDatasetJson.Fingerprint(new { SchemaVersion = 3, row.Metadata.Target,
        row.Metadata.PredictionCutoffUtc, row.FeatureEvidence, row.Features });
    public async Task<FootballResultSnapshot> BuildAsync(FootballResultDatasetRequest request, CancellationToken token = default)
    {
        var d = request.Metadata.Definition; d.Validate();
        if (d.Version != 2 || request.LabelAsOfUtc.Kind != DateTimeKind.Utc || request.LabelAsOfUtc.Ticks % 10 != 0 ||
            request.LabelAsOfUtc > d.AsOfUtc || d.Targets.Any(t => request.LabelAsOfUtc < t.PredictionCutoffUtc)) throw new ArgumentException("Metadata v2 and explicit label cutoff between prediction and dataset AsOf required.");
        var build = await metadata.BuildAsync(request.Metadata, token);
        if (build.Status != DatasetBuildStatus.Succeeded || build.SnapshotId is null) throw new InvalidOperationException("Metadata assembly denied: " + build.FailureCode);
        var baseline = await metadata.InspectAsync(build.SnapshotId.Value, token);
        FootballResultManifest manifest;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token))
        {
            if (metadata is not BetStats.Infrastructure.Datasets.PostgreSqlDatasets assembler) throw new InvalidOperationException("Transactional metadata adapter required.");
            var sameSnapshotMetadata = await assembler.AssembleFrozenAsync(baseline.Manifest.Definition, token);
            if (CanonicalDatasetJson.Fingerprint(sameSnapshotMetadata) != baseline.ManifestHash) throw new InvalidOperationException("Metadata visibility changed; retry the complete result build.");
            var rows = new List<FootballResultDatasetRow>();
            foreach (var row in baseline.Manifest.Rows)
            {
                var features = await results.ReadAsync(new(d.CompetitionId, d.SeasonId, row.PredictionCutoffUtc, d.Purpose, d.Context,
                    SourceId: row.Target.SourceId, Mode: d.Mode, ReconstructionAtUtc: d.ReconstructionAtUtc), token);
                var labels = await results.ReadAsync(new(d.CompetitionId, d.SeasonId, request.LabelAsOfUtc, d.Purpose, d.Context,
                    row.EventId, row.Target.SourceId, d.Mode, d.ReconstructionAtUtc), token);
                var vector = FootballResultFeatures.Compute(row.Target, row.PredictionCutoffUtc, features.Results);
                var entry = new FootballResultDatasetRow(row, features, labels, vector,
                    labels.Results.Where(e => e.Eligible && e.Labels is not null).Select(e => e.Labels!).OrderBy(e => e.ResultObservationId).ToArray(), "");
                rows.Add(entry with { FeatureHash = FeatureHash(entry) });
            }
            manifest = new(3, 3, 1, baseline.Manifest, baseline.ManifestHash, request.LabelAsOfUtc, rows);
            await tx.CommitAsync(token);
        }
        var content = CanonicalDatasetJson.Serialize(manifest); var hash = CanonicalDatasetJson.Hash(content);
        if (content.Length > 16 * 1024 * 1024) throw new InvalidOperationException("Result artifact bound exceeded.");
        // Recheck metadata (including coverage expiry) and all result rights before finalization.
        _ = await metadata.InspectAsync(baseline.Id, token);
        await using var publication = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({hash}, 9009))", token);
        foreach (var source in baseline.Manifest.Rows.SelectMany(r => r.History.Prepend(r.Target)).Select(e => e.SourceId)
            .Concat(Evidence(manifest).Select(e => e.Observation.SourceId)).Distinct().Order())
            await QualityPersistence.Lock(db, source, token);
        if (metadata is not BetStats.Infrastructure.Datasets.PostgreSqlDatasets frozenMetadata) throw new InvalidOperationException("Transactional metadata adapter required.");
        await frozenMetadata.EnsureCurrentFrozenAsync(baseline.Manifest, token);
        await results.EnsureCurrentAsync(Evidence(manifest), d.Purpose, d.Context, token);
        var existing = await db.FootballResultArtifacts.AsNoTracking().SingleOrDefaultAsync(a => a.Hash == hash, token);
        if (existing is not null && !existing.Content.AsSpan().SequenceEqual(content)) throw new InvalidDataException("Result content key mismatch.");
        if (existing is null)
        {
            existing = new() { Id = Guid.NewGuid(), MetadataSnapshotId = baseline.Id, Hash = hash, Content = content };
            db.Add(existing); await db.SaveChangesAsync(token);
        }
        await publication.CommitAsync(token); return new(existing.Id, hash, manifest);
    }
    public async Task<FootballResultSnapshot> InspectAsync(Guid id, CancellationToken token = default)
    {
        var artifact = await db.FootballResultArtifacts.AsNoTracking().SingleAsync(a => a.Id == id, token);
        var m = CanonicalDatasetJson.Deserialize<FootballResultManifest>(artifact.Content);
        if (m.ManifestVersion != 3 || m.FeatureSchemaVersion != 3 || m.Rows.Count is < 1 or > 100 || CanonicalDatasetJson.Hash(artifact.Content) != artifact.Hash) throw new InvalidDataException("Invalid result manifest.");
        var baseline = await metadata.InspectAsync(artifact.MetadataSnapshotId, token);
        if (baseline.ManifestHash != m.MetadataManifestHash || CanonicalDatasetJson.Fingerprint(m.MetadataManifest) != m.MetadataManifestHash) throw new InvalidDataException("Frozen metadata reference mismatch.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await results.EnsureCurrentAsync(Evidence(m), m.MetadataManifest.Definition.Purpose, m.MetadataManifest.Definition.Context, token);
        await tx.CommitAsync(token); return new(id, artifact.Hash, m);
    }
    public async Task<FootballResultVerification> VerifyAsync(Guid id, CancellationToken token = default)
    {
        var a = await db.FootballResultArtifacts.AsNoTracking().SingleAsync(a => a.Id == id, token);
        var m = CanonicalDatasetJson.Deserialize<FootballResultManifest>(a.Content);
        var integrity = m.ManifestVersion == 3 && m.FeatureSchemaVersion == 3 && CanonicalDatasetJson.Fingerprint(m.MetadataManifest) == m.MetadataManifestHash &&
            CanonicalDatasetJson.Hash(a.Content) == a.Hash && CanonicalDatasetJson.Serialize(m).AsSpan().SequenceEqual(a.Content);
        var reproducible = m.Rows.All(r => CanonicalDatasetJson.Fingerprint(FootballResultFeatures.Compute(r.Metadata.Target, r.Metadata.PredictionCutoffUtc, r.FeatureEvidence.Results)) == CanonicalDatasetJson.Fingerprint(r.Features) && FeatureHash(r) == r.FeatureHash &&
            CanonicalDatasetJson.Fingerprint(r.LabelEvidence.Results.Where(e => e.Eligible).Select(e => FootballOutcomes.Derive(e.Observation)).Where(e => e is not null).OrderBy(e => e!.ResultObservationId).ToArray()) == CanonicalDatasetJson.Fingerprint(r.Labels));
        var baseline = await metadata.VerifyAsync(a.MetadataSnapshotId, token);
        integrity &= baseline.ArtifactIntegrity; reproducible &= baseline.FeaturesReproducible;
        var authorized = baseline.CurrentlyAuthorized;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        try { await results.EnsureCurrentAsync(Evidence(m), m.MetadataManifest.Definition.Purpose, m.MetadataManifest.Definition.Context, token); }
        catch (UnauthorizedAccessException) { authorized = false; }
        await tx.CommitAsync(token);
        return new(integrity, reproducible, authorized);
    }
}
