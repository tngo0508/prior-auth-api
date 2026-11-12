namespace PriorAuth.Api.Domain;

/// <summary>
/// The plan's calendar. "Today" is the date in the plan's time zone (Plan:TimeZone, default America/Los_Angeles),
/// not the UTC date, so an approval made at 5 p.m. Pacific is not treated as made tomorrow.
/// Instants are still stored in UTC.
/// </summary>
public class PlanClock(TimeProvider time, IConfiguration config)
{
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(config["Plan:TimeZone"] ?? "America/Los_Angeles");

    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, _zone));

    /// <summary>The plan's local date of a UTC instant.</summary>
    public DateOnly LocalDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone));

    /// <summary>A UTC instant as the plan's local clock time, for showing to staff.</summary>
    public DateTime LocalTime(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);
}
