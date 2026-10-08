namespace BetStats.ArchitectureTests;
public sealed class CoverageBoundaryTests
{
    [Theory] [InlineData("BetStats.Application", "Coverage")] [InlineData("BetStats.Application", "Evaluation")] [InlineData("BetStats.Domain", "Coverage")]
    public void Coverage_time_and_evaluation_contracts_remain_provider_and_persistence_neutral(string project, string folder)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var files = Directory.GetFiles(Path.Combine(root.FullName, "src", project, folder), "*.cs"); Assert.NotEmpty(files);
        foreach (var file in files) {
            var text = File.ReadAllText(file); Assert.DoesNotContain("BetStats.Infrastructure", text); Assert.DoesNotContain("EntityFrameworkCore", text);
            Assert.DoesNotContain("HttpClient", text); Assert.DoesNotContain("Microsoft.AspNetCore", text);
            if (project == "BetStats.Domain") Assert.DoesNotContain("BetStats.Application", text);
        }
    }
}
