namespace BetStats.ArchitectureTests;

public sealed class DashboardBoundaryTests
{
    [Fact]
    public void Dashboard_port_exposes_reads_only_and_adapter_does_not_execute_or_save()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var adapter = File.ReadAllText(Path.Combine(root.FullName, "src", "BetStats.Infrastructure", "Dashboard", "PostgreSqlDashboardQueries.cs"));
        foreach (var forbidden in new[] { "SaveChanges", "ExecuteSql", "BacktestExecutor", ".RunAsync(", ".PlanAsync(", ".VerifyAsync(", ".BuildAsync(", ".Simulate(" }) Assert.DoesNotContain(forbidden, adapter);
        var webFiles = Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "BetStats.Web"), "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));
        foreach (var file in webFiles) { var text = File.ReadAllText(file); Assert.DoesNotContain("BetStats.Infrastructure", text); Assert.DoesNotContain("BetStatsDbContext", text); }
    }
}
