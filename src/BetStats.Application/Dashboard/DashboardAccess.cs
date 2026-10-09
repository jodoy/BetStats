using System.Net;

namespace BetStats.Application.Dashboard;

public static class DashboardAccess
{
    public static bool LoopbackHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    public static bool Allowed(bool development, IPAddress? remote, string host, string? origin = null) =>
        development && remote is not null && IPAddress.IsLoopback(remote) && LoopbackHost(host) &&
        (string.IsNullOrEmpty(origin) || Uri.TryCreate(origin, UriKind.Absolute, out var uri) && LoopbackHost(uri.Host));
    public static Uri ApiUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !LoopbackHost(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("A loopback API base URL without credentials is required.");
        return uri;
    }
}
