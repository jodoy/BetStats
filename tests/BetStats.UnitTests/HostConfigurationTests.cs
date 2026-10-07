using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace BetStats.UnitTests;

public sealed class HostConfigurationTests
{
    // Exercise the default builders used by Worker and API/Web without starting
    // servers. There is no Domain/Application business logic to test yet.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Environment_variables_override_json_settings(bool webHost)
    {
        WithSettings(webHost, [], value => Assert.Equal("environment-value", value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Command_line_overrides_environment_settings(bool webHost)
    {
        WithSettings(webHost, ["--ConnectionStrings:FoundationProbe=command-line-value"],
            value => Assert.Equal("command-line-value", value));
    }

    private static void WithSettings(bool webHost, string[] args, Action<string?> assert)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"betstats-config-{Guid.NewGuid():N}");
        // A unique section prevents collisions with developer configuration or
        // other tests. Only non-secret probe values are written.
        var section = $"Probe{Guid.NewGuid():N}";
        var variable = $"ConnectionStrings__{section}";
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "appsettings.json"),
                JsonSerializer.Serialize(new { ConnectionStrings = new Dictionary<string, string> { [section] = "json-value" } }));
            Environment.SetEnvironmentVariable(variable, "environment-value");
            var actualArgs = args.Select(arg => arg.Replace("FoundationProbe", section, StringComparison.Ordinal)).ToArray();

            if (webHost)
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    ContentRootPath = directory,
                    EnvironmentName = Environments.Production,
                    Args = actualArgs
                });
                using var configuration = builder.Configuration;
                AssertJsonValue(configuration, section);
                assert(configuration.GetConnectionString(section));
            }
            else
            {
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    ContentRootPath = directory,
                    EnvironmentName = Environments.Production,
                    Args = actualArgs
                });
                using var configuration = builder.Configuration;
                AssertJsonValue(configuration, section);
                assert(configuration.GetConnectionString(section));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertJsonValue(IConfigurationRoot configuration, string section)
    {
        var json = configuration.Providers.First(provider =>
            provider is Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider);
        Assert.True(json.TryGet($"ConnectionStrings:{section}", out var value));
        Assert.Equal("json-value", value);
    }
}
