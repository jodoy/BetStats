namespace BetStats.Domain.Common;

internal static class Require
{
    public static Guid Id(Guid value) => value != Guid.Empty ? value : throw new ArgumentException("UUID must not be empty.");
    public static string Text(string value, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Length <= maxLength ? value : throw new ArgumentException($"Text exceeds {maxLength} characters.");
    }
    public static T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : throw new ArgumentException("Unknown enum value.");
    public static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc && value.Ticks % 10 == 0
        ? value : throw new ArgumentException("Timestamp must be UTC with PostgreSQL microsecond precision.");
    public static DateTime? Utc(DateTime? value) => value is { } date ? Utc(date) : null;
    public static void That(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}
