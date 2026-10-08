namespace BetStats.ArchitectureTests;

public sealed class BacktestBoundaryTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BetStats.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root required.");
    }
    [Fact] public void Prediction_provider_uses_only_canonical_input_and_no_labels_or_infrastructure()
    {
        var contracts = File.ReadAllText(Path.Combine(Root(), "src/BetStats.Application/Evaluation/BacktestContracts.cs"));
        var input = contracts[contracts.IndexOf("public sealed record PredictionInput", StringComparison.Ordinal)..contracts.IndexOf("public sealed record PredictionValue", StringComparison.Ordinal)];
        Assert.Contains("FootballResultFeatureVector", input); Assert.DoesNotContain("Labels", input); Assert.DoesNotContain("FootballResultReport", input); Assert.DoesNotContain("Provider", input);
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(Root(), "src/BetStats.Application/BetStats.Application.csproj"));
        Assert.All(project.Descendants("ProjectReference"), reference => Assert.EndsWith("BetStats.Domain.csproj", reference.Attribute("Include")!.Value));
    }
    [Fact] public void Backtesting_remains_an_explicit_worker_operation_without_api_expansion()
    {
        var root = Root(); Assert.DoesNotContain("Backtest", File.ReadAllText(Path.Combine(root, "src/BetStats.Api/Program.cs")));
        var command = File.ReadAllText(Path.Combine(root, "src/BetStats.Infrastructure/Evaluation/BacktestOperatorCommand.cs"));
        Assert.Contains("if (!development)", command); Assert.Contains("Backtest:Approve", command);
        var worker = File.ReadAllText(Path.Combine(root, "src/BetStats.Worker/Program.cs")); Assert.Contains("Backtest:Action", worker);
        Assert.DoesNotContain("Migrate", worker); Assert.DoesNotContain("Seed", worker);
    }
}
