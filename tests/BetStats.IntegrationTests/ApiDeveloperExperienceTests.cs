using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BetStats.Application.Sports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BetStats.IntegrationTests;

public sealed class ApiDeveloperExperienceTests
{
    private sealed class Host(string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(environment);
    }
    private static HttpClient Client(Host host) => host.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });

    [Fact]
    public async Task Visual_studio_profile_opens_available_development_documentation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(root.FullName, "src", "BetStats.Api", "Properties", "launchSettings.json")));
        var profile = settings.RootElement.GetProperty("profiles").GetProperty("http");
        Assert.Equal("Project", profile.GetProperty("commandName").GetString());
        Assert.True(profile.GetProperty("launchBrowser").GetBoolean());
        Assert.Equal("swagger", profile.GetProperty("launchUrl").GetString());
        var environment = profile.GetProperty("environmentVariables").GetProperty("ASPNETCORE_ENVIRONMENT").GetString();
        Assert.Equal("Development", environment);
        await using var host = new Host(environment!);
        using var client = host.CreateClient(new() { BaseAddress = new(profile.GetProperty("applicationUrl").GetString()!) });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/" + profile.GetProperty("launchUrl").GetString())).StatusCode);
    }

    [Theory]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger/v1/swagger.json")]
    public async Task Documentation_is_available_only_in_development(string route)
    {
        await using var development = new Host("Development"); using var dev = Client(development);
        Assert.Equal(HttpStatusCode.OK, (await dev.GetAsync(route)).StatusCode);
        await using var production = new Host("Production"); using var prod = Client(production);
        Assert.Equal(HttpStatusCode.NotFound, (await prod.GetAsync(route)).StatusCode);
    }
    [Fact]
    public async Task OpenApi_has_metadata_read_schemas_and_no_administrative_mutations()
    {
        await using var host = new Host("Development"); using var client = Client(host);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var root = document.RootElement;
        Assert.Equal("BetStats API", root.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("v1", root.GetProperty("info").GetProperty("version").GetString());
        Assert.Equal("Multi-sport analytics and historical evidence API.", root.GetProperty("info").GetProperty("description").GetString());
        var paths = root.GetProperty("paths");
        Assert.Equal(7, paths.EnumerateObject().Count());
        foreach (var path in paths.EnumerateObject())
        {
            var operation = path.Value.GetProperty("get");
            Assert.False(string.IsNullOrEmpty(operation.GetProperty("summary").GetString()));
            Assert.False(string.IsNullOrEmpty(operation.GetProperty("description").GetString()));
            Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
            Assert.DoesNotContain(path.Value.EnumerateObject(), p => p.Name is "post" or "put" or "patch" or "delete");
        }
        Assert.True(paths.GetProperty("/api/v1/sports").GetProperty("get").GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(root.GetProperty("components").GetProperty("schemas").TryGetProperty("PublicSportPage", out _));
        Assert.All(paths.GetProperty("/api/v1/sports").GetProperty("get").GetProperty("parameters").EnumerateArray(), p => Assert.False(p.TryGetProperty("required", out var required) && required.GetBoolean()));
    }
    [Fact]
    public async Task Safe_reads_work_without_a_database_and_have_stable_pagination()
    {
        await using var host = new Host("Development"); using var client = Client(host);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal("Healthy", (await client.GetFromJsonAsync<ApiHealth>("/api/v1/health"))!.Status);
        var first = (await client.GetFromJsonAsync<PublicSportPage>("/api/v1/sports?offset=0&limit=2"))!;
        var second = (await client.GetFromJsonAsync<PublicSportPage>("/api/v1/sports?offset=2&limit=2"))!;
        Assert.Equal(4, first.Total); Assert.Equal(2, first.Items.Count); Assert.Equal(2, second.Items.Count);
        Assert.Equal(4, first.Items.Concat(second.Items).Select(x => x.Id).Distinct().Count());
        Assert.Equal(first.Items.Select(x => x.Code).Order(StringComparer.Ordinal), first.Items.Select(x => x.Code));
        Assert.Empty((await client.GetFromJsonAsync<PublicSportPage>("/api/v1/sports?offset=4"))!.Items);
        var body = await client.GetStringAsync("/api/v1/sports");
        Assert.DoesNotContain("Raw", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Provider", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsJsonAsync("/api/v1/sports", new { })).StatusCode);
    }
    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?offset=-1")]
    [InlineData("?limit=wrong")]
    public async Task Invalid_queries_return_problem_details(string query)
    {
        await using var host = new Host("Development"); using var client = Client(host);
        var response = await client.GetAsync("/api/v1/sports" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(400, (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Status);
    }
    [Theory]
    [InlineData("/api/v1/events")]
    [InlineData("/api/v1/competitions")]
    [InlineData("/api/v1/seasons")]
    [InlineData("/api/v1/datasets")]
    [InlineData("/api/v1/coverage/report")]
    public async Task Provider_derived_and_operator_surfaces_are_not_public(string route)
    {
        await using var host = new Host("Development"); using var client = Client(host);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(route, new { })).StatusCode);
    }
}
