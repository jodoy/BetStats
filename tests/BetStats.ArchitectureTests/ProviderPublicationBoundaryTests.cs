namespace BetStats.ArchitectureTests;

public sealed class ProviderPublicationBoundaryTests
{
    private static string Read(string relative)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root); return File.ReadAllText(Path.Combine(root.FullName, relative));
    }
    [Fact] public void Publication_uses_original_raw_and_existing_pipeline_without_http_or_prediction_execution()
    {
        var publisher = Read("src/BetStats.Infrastructure/Pipeline/ProviderPublication.cs");
        foreach (var forbidden in new[] { "HttpClient", "HttpMessageInvoker", "IContentProviderAdapter", "StageAsync", "CaptureAsync", "BacktestExecutor", "FootballPredictor" }) Assert.DoesNotContain(forbidden, publisher);
        Assert.Contains("PostgreSqlPipeline", publisher); Assert.Contains("store.ReadAsync", publisher);
        Assert.Contains("BeginTransactionAsync", publisher); Assert.Contains("jobs.CheckAsync", publisher);
    }
    [Fact] public void Publication_monitoring_depends_only_on_read_ports_and_never_dispatches()
    {
        var api = Read("src/BetStats.Api/DashboardEndpoints.cs");
        Assert.Contains("IProviderPublicationReports", api); Assert.DoesNotContain("PublishAsync", api); Assert.DoesNotContain("RecoverAsync", api);
        var view = Read("src/BetStats.Web/Components/ProviderPublicationView.razor");
        Assert.DoesNotContain("@inject", view); Assert.DoesNotContain("@onclick", view);
    }
}
