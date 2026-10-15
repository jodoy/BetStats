namespace BetStats.ArchitectureTests;

[Trait("Category", "Security")]
public sealed class PrivateInspectionBoundaryTests
{
    [Fact]
    public void Private_audience_is_host_selected_only_after_existing_network_checks()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var endpoints = File.ReadAllText(Path.Combine(root.FullName, "src/BetStats.Api/DashboardEndpoints.cs"));
        Assert.True(endpoints.IndexOf("DashboardAccess.Allowed", StringComparison.Ordinal) < endpoints.IndexOf("inspection.Begin()", StringComparison.Ordinal));
        Assert.Contains("finally { inspection.End(); }", endpoints);
        Assert.DoesNotContain("[FromQuery] InspectionAudience", endpoints);
        foreach (var file in new[] { "Program.cs", "DevelopmentFootballEndpoints.cs" })
            Assert.DoesNotContain("PrivateOperatorInspection", File.ReadAllText(Path.Combine(root.FullName, "src/BetStats.Api", file)));
    }
}
