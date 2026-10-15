namespace BetStats.ArchitectureTests;

[Trait("Category", "Security")]
public sealed class AcquisitionBoundaryTests
{
    [Fact] public void Acquisition_has_no_live_transport_or_startup_dispatch_and_monitoring_is_get_only()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var registration = File.ReadAllText(Path.Combine(root.FullName, "src/BetStats.Infrastructure/DependencyInjection.cs"));
        Assert.DoesNotContain("AddHttpClient", registration); Assert.DoesNotContain("IFootballApiTransport,", registration);
        var endpoints = File.ReadAllText(Path.Combine(root.FullName, "src/BetStats.Api/DashboardEndpoints.cs"));
        foreach (var route in new[] { "acquisition", "prospective", "acquisition-status" }) Assert.Contains("group.MapGet(\"/" + route + "/", endpoints);
        foreach (var forbidden in new[] { "MapPost", "ExecuteAsync", "BuildAsync", "PlanAsync", "RunAsync" }) Assert.DoesNotContain(forbidden, endpoints);
        var source = File.ReadAllText(Path.Combine(root.FullName, "src/BetStats.Infrastructure/Pipeline/FootballAcquisition.cs"));
        Assert.DoesNotContain("FootballPredictor", source); Assert.DoesNotContain("BacktestExecutor", source); Assert.DoesNotContain("HttpClient", source);
        var worker = File.ReadAllText(Path.Combine(root.FullName, "src/BetStats.Infrastructure/Pipeline/PipelineOperatorCommand.cs"));
        Assert.Contains("if (!development)", worker); Assert.Contains("acquisition-plan", worker);
    }
}
