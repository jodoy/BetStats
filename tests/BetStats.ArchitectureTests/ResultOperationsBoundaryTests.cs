using System.Xml.Linq;

namespace BetStats.ArchitectureTests;

public sealed class ResultOperationsBoundaryTests
{
    [Fact]
    public void Result_governance_and_operations_keep_existing_project_dependency_directions()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var (project, allowed) in new[] { ("Domain", Array.Empty<string>()), ("Application", new[] { "Domain" }), ("Infrastructure", new[] { "Domain", "Application" }), ("Web", new[] { "Domain", "Application" }) })
        {
            var file = Path.Combine(root.FullName, "src", "BetStats." + project, "BetStats." + project + ".csproj");
            foreach (var reference in XDocument.Load(file).Descendants("ProjectReference"))
                Assert.Contains(Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')).Replace("BetStats.", ""), allowed);
        }
        Assert.DoesNotContain("Results:Action", File.ReadAllText(Path.Combine(root.FullName, "src", "BetStats.Api", "Program.cs")));
    }
}
