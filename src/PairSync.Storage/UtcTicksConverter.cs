using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PairSync.Storage;

/// <summary>Stores <see cref="DateTime"/> as UTC ticks and reads it back with <see cref="DateTimeKind.Utc"/>.</summary>
internal sealed class UtcTicksConverter() : ValueConverter<DateTime, long>(
    value => value.ToUniversalTime().Ticks,
    ticks => new DateTime(ticks, DateTimeKind.Utc));
