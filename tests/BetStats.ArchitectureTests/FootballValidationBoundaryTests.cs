namespace BetStats.ArchitectureTests;

[Trait("Category", "Security")]
public sealed class FootballValidationBoundaryTests
{
    [Fact] public void Validation_http_surface_is_read_only_and_never_dispatches_pipeline()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var endpoints = File.ReadAllText(Path.Combine(root.FullName, "src/BetStats.Api/DashboardEndpoints.cs"));
        Assert.Contains("group.MapGet(\"/validation/{executionId:guid}\"", endpoints);
        Assert.Contains("reports.ReadAsync(executionId, InspectionAudience.PrivateOperatorInspection", endpoints);
        foreach (var forbidden in new[] { "PipelineOperatorCommand", "BuildAsync", "AcquireAsync", "RunAsync", "MapPost" }) Assert.DoesNotContain(forbidden, endpoints);
        var worker = File.ReadAllText(Path.Combine(root.FullName, "src/BetStats.Infrastructure/Pipeline/PipelineOperatorCommand.cs"));
        Assert.Contains("if (!development)", worker); Assert.Contains("verify-validation", worker);
        var contracts = File.ReadAllText(Path.Combine(root.FullName, "src/BetStats.Application/Pipeline/FootballValidation.cs"));
        var readPort = contracts.Split("public interface IFootballValidationReports")[1].Split("public static class")[0];
        Assert.Contains("ReadAsync", readPort); Assert.DoesNotContain("Build", readPort); Assert.DoesNotContain("Run", readPort);
    }
}
