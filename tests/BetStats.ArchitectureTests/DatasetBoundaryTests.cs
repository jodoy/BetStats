namespace BetStats.ArchitectureTests;

public sealed class DatasetBoundaryTests
{
    [Fact]
    public void Dataset_contracts_and_calculators_have_no_database_or_transport_dependencies()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var files = Directory.GetFiles(Path.Combine(root.FullName, "src", "BetStats.Application", "Datasets"), "*.cs");
        Assert.NotEmpty(files);
        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("using BetStats.Infrastructure", text); Assert.DoesNotContain("Microsoft.EntityFrameworkCore", text);
            Assert.DoesNotContain("Microsoft.AspNetCore", text); Assert.DoesNotContain("HttpClient", text);
        }
        Assert.Contains("interface IDatasets", File.ReadAllText(Path.Combine(root.FullName, "src", "BetStats.Application", "Datasets", "DatasetContracts.cs")));
    }
}
