using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetStats.Application.Dashboard;

namespace BetStats.Web;

public sealed class DashboardClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public async Task<T> ReadAsync<T>(string resource, DashboardFilter filter, CancellationToken token = default)
    {
        filter.Validate();
        var query = new Dictionary<string, string?>
        {
            ["Competition"] = filter.Competition?.ToString(),
            ["Season"] = filter.Season?.ToString(),
            ["Team"] = filter.Team?.ToString(),
            ["From"] = filter.From?.ToString("yyyy-MM-dd"),
            ["To"] = filter.To?.ToString("yyyy-MM-dd"),
            ["Status"] = filter.Status,
            ["Offset"] = filter.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Limit"] = filter.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var uri = "api/v1/dashboard/" + resource + "?" + string.Join("&", query.Where(p => p.Value is not null).Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value!)));
        using var response = await http.GetAsync(uri, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, token) ?? throw new InvalidDataException("Empty API response.");
    }
}
