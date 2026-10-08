using System.Diagnostics;
using System.Text.Json;

namespace BetStats.ArchitectureTests;

public sealed class ProjectDependencyTests
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedDependencies =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["BetStats.Domain"] = [],
            ["BetStats.Application"] = ["BetStats.Domain"],
            ["BetStats.Infrastructure"] = ["BetStats.Application", "BetStats.Domain"],
            ["BetStats.Api"] = ["BetStats.Application", "BetStats.Infrastructure", "BetStats.Domain"],
            ["BetStats.Worker"] = ["BetStats.Application", "BetStats.Infrastructure", "BetStats.Domain"],
            ["BetStats.Web"] = ["BetStats.Application", "BetStats.Domain"]
        };

    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public async Task Production_projects_follow_dependency_rules(string configuration)
    {
        var root = FindRepositoryRoot();
        var projects = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);
        Assert.Equal(AllowedDependencies.Keys.Order(), projects.Select(Path.GetFileNameWithoutExtension).Order());
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            using var evaluation = await Evaluate(project, configuration, root);
            var name = Path.GetFileNameWithoutExtension(project);
            var items = evaluation.RootElement.GetProperty("Items");
            var references = items.GetProperty("ProjectReference").EnumerateArray()
                .Select(item => item.GetProperty("FullPath").GetString()!).ToArray();

            foreach (var reference in references)
            {
                Assert.Contains(reference, projects);
                Assert.True(IsAllowed(name, Path.GetFileNameWithoutExtension(reference)),
                    $"Forbidden project dependency: {name} -> {reference} ({configuration}). See ADR 0013.");
            }

            // A binary reference could otherwise bypass the project graph.
            Assert.Empty(items.GetProperty("Reference").EnumerateArray());
            // Composition roots register Infrastructure but must not add their
            // own EF Core/Npgsql implementation package dependencies (ADR 0014).
            if (name != "BetStats.Infrastructure")
            {
                Assert.DoesNotContain(items.GetProperty("PackageReference").EnumerateArray(), item =>
                    IsPersistencePackage(item.GetProperty("Identity").GetString()!));
            }

            graph[name] = references.Select(reference => Path.GetFileNameWithoutExtension(reference)).ToArray();
        }

        foreach (var project in graph.Keys)
        {
            AssertAcyclic(project, graph, []);
        }
    }

    [Theory]
    [InlineData("BetStats.Domain", "BetStats.Application")]
    [InlineData("BetStats.Domain", "BetStats.Infrastructure")]
    [InlineData("BetStats.Application", "BetStats.Infrastructure")]
    [InlineData("BetStats.Application", "BetStats.Api")]
    [InlineData("BetStats.Web", "BetStats.Infrastructure")]
    [InlineData("BetStats.Worker", "BetStats.Api")]
    public void Dependency_policy_rejects_forbidden_edges(string source, string target)
    {
        Assert.False(IsAllowed(source, target));
    }

    private static bool IsAllowed(string source, string target) =>
        AllowedDependencies.TryGetValue(source, out var allowed) && allowed.Contains(target, StringComparer.Ordinal);

    private static bool IsPersistencePackage(string name) =>
        name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase);

    private static void AssertAcyclic(string project, IReadOnlyDictionary<string, string[]> graph, HashSet<string> path)
    {
        Assert.True(path.Add(project), $"Circular project dependency involving {project}.");
        foreach (var dependency in graph[project])
        {
            AssertAcyclic(dependency, graph, path);
        }
        path.Remove(project);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BetStats.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Run architecture tests inside the BetStats source checkout.");
    }

    private static async Task<JsonDocument> Evaluate(string project, string configuration, string root)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "msbuild", project, "-nologo", $"-property:Configuration={configuration}",
            "-getItem:ProjectReference,PackageReference,Reference"
        })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start MSBuild.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"MSBuild evaluation timed out for {project}.");
        }
        Assert.True(process.ExitCode == 0, $"MSBuild evaluation failed for {project}: {await error}\n{await output}");
        return JsonDocument.Parse(await output);
    }
}
