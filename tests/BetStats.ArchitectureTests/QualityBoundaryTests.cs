namespace BetStats.ArchitectureTests;

public sealed class QualityBoundaryTests
{
    [Fact]
    public void Quality_contracts_and_domain_do_not_import_transport_or_persistence()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var project in new[] { "BetStats.Domain", "BetStats.Application" })
        {
            var files = Directory.GetFiles(Path.Combine(root.FullName, "src", project, "Quality"), "*.cs");
            Assert.NotEmpty(files);
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain("using BetStats.Infrastructure", text);
                Assert.DoesNotContain("using Microsoft.EntityFrameworkCore", text);
                Assert.DoesNotContain("using Microsoft.AspNetCore", text);
            }
        }
        Assert.Contains("interface IIdentityReview", File.ReadAllText(Path.Combine(root.FullName, "src", "BetStats.Application", "Quality", "QualityContracts.cs")));
    }
}
