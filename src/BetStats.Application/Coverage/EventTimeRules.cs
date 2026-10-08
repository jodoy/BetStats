using BetStats.Domain.Coverage;

namespace BetStats.Application.Coverage;

public static class EventTimeRules
{
    public static EventTimeResolution Resolve(EventTimeValue value)
    {
        if (!Enum.IsDefined(value.Precision) || value.OffsetMinutes is < -840 or > 840 || value.TimeZoneId?.Length > 100)
            throw new ArgumentException("Invalid precision or offset.");
        if (value.Precision == EventTimePrecision.Unknown)
        {
            if (value.LocalDate is not null || value.LocalTime is not null || value.SourceUtcInstant is not null || value.OffsetMinutes is not null || value.TimeZoneId is not null)
                throw new ArgumentException("Unknown precision cannot assert an instant.");
            return new(null, value.Precision, "event_time_unknown");
        }
        if (value.Precision == EventTimePrecision.DateOnly)
        {
            if (value.LocalDate is null || value.LocalTime is not null || value.SourceUtcInstant is not null || value.OffsetMinutes is not null)
                throw new ArgumentException("Date-only evidence cannot assert kickoff.");
            return new(null, value.Precision, "date_only_no_kickoff");
        }
        if (value.LocalDate is null || value.LocalTime is null || value.LocalTime.Value.Ticks % TimeSpan.TicksPerSecond != 0 ||
            value.Precision == EventTimePrecision.Minute && value.LocalTime.Value.Second != 0) throw new ArgumentException("Local date/time must match precision.");
        var local = value.LocalDate.Value.ToDateTime(value.LocalTime.Value, DateTimeKind.Unspecified);
        TimeZoneInfo? zone = null;
        if (value.TimeZoneId is not null)
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(value.TimeZoneId); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return new(null, value.Precision, "timezone_unsupported"); }
            if (zone.IsInvalidTime(local)) return new(null, value.Precision, "dst_gap");
            if (zone.IsAmbiguousTime(local) && value.OffsetMinutes is null) return new(null, value.Precision, "dst_ambiguous");
        }
        if (zone is null && value.OffsetMinutes is null) return new(null, value.Precision, "timezone_context_missing");
        var offset = value.OffsetMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : zone!.GetUtcOffset(local);
        if (zone is not null && (zone.IsAmbiguousTime(local) ? !zone.GetAmbiguousTimeOffsets(local).Contains(offset) : zone.GetUtcOffset(local) != offset))
            return new(null, value.Precision, "offset_timezone_conflict");
        DateTime utc;
        try { utc = new DateTimeOffset(local, offset).UtcDateTime; }
        catch (ArgumentException) { return new(null, value.Precision, "utc_overflow"); }
        if (value.SourceUtcInstant is { } supplied && (supplied.Kind != DateTimeKind.Utc || supplied != utc)) return new(null, value.Precision, "source_utc_conflict");
        return new(utc, value.Precision, zone?.IsAmbiguousTime(local) == true ? "explicit_offset_disambiguated" : "source_context_resolved");
    }
    public static bool Conflicts(EventTimeValue a, EventTimeValue b)
    {
        var x = Resolve(a); var y = Resolve(b);
        if (x.UtcInstant is not null && y.UtcInstant is not null) return x.UtcInstant != y.UtcInstant;
        return a.LocalDate is not null && b.LocalDate is not null && a.LocalDate != b.LocalDate &&
            (a.TimeZoneId == b.TimeZoneId || a.Precision == EventTimePrecision.DateOnly || b.Precision == EventTimePrecision.DateOnly);
    }
}
