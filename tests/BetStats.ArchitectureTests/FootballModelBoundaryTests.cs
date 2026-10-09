namespace BetStats.ArchitectureTests;

public sealed class FootballModelBoundaryTests
{
    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Repository root required.");
    }
    [Fact] public void Models_have_no_transport_persistence_or_evaluation_label_dependencies()
    {
        var text = File.ReadAllText(Path.Combine(Root(), "src/BetStats.Application/Models/FootballModels.cs"));
        foreach (var forbidden in new[] { "BetStats.Infrastructure", "EntityFrameworkCore", "HttpClient", "LabelEvidence", "FootballResultReport", "ProviderIdentity", "SourceEventReference" }) Assert.DoesNotContain(forbidden, text);
        Assert.Contains("IHistoricalPredictionProvider", text); Assert.Contains("AvailableAtUtc", text); Assert.Contains("RecordedAtUtc", text);
    }
    [Fact] public void Model_operations_are_development_only_and_reuse_backtest_recovery()
    {
        var root = Root(); var text = File.ReadAllText(Path.Combine(root, "src/BetStats.Infrastructure/Evaluation/ModelOperatorCommand.cs"));
        Assert.Contains("if (!development)", text); Assert.Contains("BacktestOperatorCommand.RunAsync", text); Assert.Contains("VerifyAsync(id, true", text);
        var api = File.ReadAllText(Path.Combine(root, "src/BetStats.Api/Program.cs")); Assert.DoesNotContain("FootballPredictor", api); Assert.DoesNotContain("ModelOperatorCommand", api);
        var worker = File.ReadAllText(Path.Combine(root, "src/BetStats.Worker/Program.cs")); Assert.Contains("Model:Action", worker); Assert.DoesNotContain("Migrate", worker); Assert.DoesNotContain("Seed", worker);
    }
}
