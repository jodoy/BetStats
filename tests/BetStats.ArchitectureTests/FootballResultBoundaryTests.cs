namespace BetStats.ArchitectureTests;

public sealed class FootballResultBoundaryTests
{
    [Fact]
    public void Result_core_has_no_provider_transport_or_persistence_implementation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var project in new[] { "BetStats.Domain", "BetStats.Application" })
        foreach (var file in Directory.GetFiles(Path.Combine(root.FullName, "src", project, "Football"), "*.cs"))
        {
            var code = File.ReadAllText(file);
            Assert.DoesNotContain("BetStats.Infrastructure", code); Assert.DoesNotContain("Microsoft.EntityFrameworkCore", code);
            Assert.DoesNotContain("HttpClient", code); Assert.DoesNotContain("FTHG", code);
        }
        var resultCode = File.ReadAllText(Path.Combine(root.FullName, "src", "BetStats.Application", "Football", "FootballResultFeatures.cs"));
        Assert.Contains("FeatureEvidence", resultCode); Assert.Contains("LabelEvidence", resultCode);
    }
}
