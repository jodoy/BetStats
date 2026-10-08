using System.Text.RegularExpressions;
using BetStats.Domain.Common;

namespace BetStats.Domain.Sports;

public sealed class Sport
{
    private Sport() { }
    public Sport(Guid id, string code, string displayName)
    {
        Id = Require.Id(id);
        Code = Require.Text(code, 50);
        Require.That(Regex.IsMatch(code, @"\A[a-z][a-z0-9-]*\z", RegexOptions.CultureInvariant), "Sport code must be a lowercase identifier.");
        DisplayName = Require.Text(displayName, 200);
    }
    public Guid Id { get; private set; }
    public string Code { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
}

public static class ReferenceSports
{
    public static IReadOnlyList<Sport> All =>
    [
        new(Guid.Parse("10000000-0000-0000-0000-000000000001"), "football", "Football"),
        new(Guid.Parse("10000000-0000-0000-0000-000000000002"), "tennis", "Tennis"),
        new(Guid.Parse("10000000-0000-0000-0000-000000000003"), "basketball", "Basketball"),
        new(Guid.Parse("10000000-0000-0000-0000-000000000004"), "ice-hockey", "IceHockey")
    ];
}
