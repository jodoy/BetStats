namespace BetStats.ArchitectureTests;

[Trait("Category", "Security")]
public sealed class ApiFootballBoundaryTests
{
    [Fact] public void Unapproved_provider_boundary_has_no_runtime_registration_or_execution_route()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var relative in new[] { "src/BetStats.Infrastructure/DependencyInjection.cs", "src/BetStats.Infrastructure/Pipeline/PipelineWork.cs",
            "src/BetStats.Api/DashboardEndpoints.cs", "src/BetStats.Worker/Program.cs" })
            Assert.DoesNotContain("ApiFootballFixtureAdapter", File.ReadAllText(Path.Combine(root.FullName, relative)));
        var reader = File.ReadAllText(Path.Combine(root.FullName, "src/BetStats.Infrastructure/Ingestion/ApiFootballFixtureReader.cs"));
        foreach (var forbidden in new[] { "DbContext", "SaveChanges", "FootballPredictor", "BacktestExecutor", "HttpClient" }) Assert.DoesNotContain(forbidden, reader);
        var page = File.ReadAllText(Path.Combine(root.FullName, "src/BetStats.Web/Components/ProviderReadiness.razor"));
        Assert.DoesNotContain("@inject", page); Assert.DoesNotContain("OnInitialized", page);
    }
}
