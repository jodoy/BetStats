namespace BetStats.ArchitectureTests;

[Trait("Category", "Security")]
public sealed class ProviderActivationBoundaryTests
{
    private static string Read(string path)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root); return File.ReadAllText(Path.Combine(root.FullName, path));
    }
    [Fact] public void Activation_api_has_only_read_port_and_no_provider_mutations_or_transport()
    {
        var api = Read("src/BetStats.Api/DashboardEndpoints.cs");
        Assert.Contains("IProviderActivationReports", api);
        foreach (var forbidden in new[] { "MapPost", "RegisterAsync", "SetEnabledAsync", "ExecuteAsync", "ApiFootballFixtureAdapter" }) Assert.DoesNotContain(forbidden, api);
        var view = Read("src/BetStats.Web/Components/ProviderActivationView.razor");
        Assert.DoesNotContain("@inject", view); Assert.DoesNotContain("@onclick", view);
    }
    [Fact] public void Transport_registration_has_fixed_safety_defaults_and_does_not_send_at_startup()
    {
        var di = Read("src/BetStats.Infrastructure/DependencyInjection.cs");
        Assert.Contains("AllowAutoRedirect = false", di); Assert.Contains("UseCookies = false", di); Assert.Contains("UseProxy = false", di);
        Assert.DoesNotContain("SendAsync", di);
        var activation = Read("src/BetStats.Infrastructure/Pipeline/ProviderActivation.cs");
        Assert.Contains("PostgreSqlPipeline", activation); Assert.Contains("CaptureProviderAsync", activation);
        foreach (var forbidden in new[] { "PeriodicTimer", "BackgroundService", "BacktestExecutor", "FootballPredictor" }) Assert.DoesNotContain(forbidden, activation);
    }
}
