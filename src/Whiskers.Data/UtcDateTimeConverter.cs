using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Whiskers.Services.Persistence;

/// <summary>
/// Normalizes every <see cref="DateTime"/> to UTC on the way to the database and marks it UTC on the way
/// back (stableDB.md step 2 / difference U2). Writers already use <c>DateTime.UtcNow</c>, but values read
/// back from SQLite come as <see cref="DateTimeKind.Unspecified"/>, which Npgsql rejects for
/// <c>timestamp with time zone</c>. Registered globally in <see cref="MetricsDbContext"/> so both
/// providers behave identically; for SQLite the stored representation is unchanged (still UTC text).
/// </summary>
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}
