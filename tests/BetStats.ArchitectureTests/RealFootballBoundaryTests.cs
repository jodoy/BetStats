namespace BetStats.ArchitectureTests;

public sealed class RealFootballBoundaryTests
{
    [Fact]
    public void Real_history_is_only_explicit_worker_work_and_does_not_register_http_transport()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var host in new[] { "Api", "Web" })
            Assert.DoesNotContain("FootballHistory:Action", File.ReadAllText(Path.Combine(root.FullName, "src", "BetStats." + host, "Program.cs")));
        var registration = File.ReadAllText(Path.Combine(root.FullName, "src", "BetStats.Infrastructure", "DependencyInjection.cs"));
        Assert.DoesNotContain("AddHttpClient", registration);
        var worker = File.ReadAllText(Path.Combine(root.FullName, "src", "BetStats.Worker", "Program.cs"));
        Assert.Contains("FootballHistory:Action", worker); Assert.Contains("builder.Environment.IsDevelopment()", worker);
    }
}
