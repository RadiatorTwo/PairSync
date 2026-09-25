using System.Globalization;
using PairSync.Desktop.Resources;

namespace PairSync.Desktop.ViewModels;

/// <summary>Numbers as the mockups show them: decimal units with three significant digits ("4.69 GB", "38.2 GB").</summary>
public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static string Bytes(long bytes) => Bytes(bytes, UnitOf(bytes));

    /// <summary>"38.2 / 92.0 GB": both in the unit of the total.</summary>
    public static string Progress(long done, long total)
    {
        var unit = UnitOf(total);
        return $"{Number(done, unit)} / {Bytes(total, unit)}";
    }

    public static string Rate(double bytesPerSecond) =>
        string.Format(Culture, Strings.Transfer_Rate, Bytes((long)bytesPerSecond));

    public static string Count(int value) => value.ToString("N0", Culture);

    public static string Files(int count) =>
        count == 1 ? Strings.Files_One : string.Format(Culture, Strings.Files_Many, Count(count));

    public static string Items(int count) =>
        count == 1 ? Strings.Items_One : string.Format(Culture, Strings.Items_Many, Count(count));

    /// <summary>"9 min left", rounded up so a transfer never shows "0 min left".</summary>
    public static string Remaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromSeconds(90))
            return string.Format(Culture, Strings.Time_SecondsLeft, Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)));
        if (remaining < TimeSpan.FromHours(1))
            return string.Format(Culture, Strings.Time_MinutesLeft, (int)Math.Ceiling(remaining.TotalMinutes));
        return string.Format(Culture, Strings.Time_HoursLeft, (int)remaining.TotalHours, remaining.Minutes);
    }

    /// <summary>"3 h ago".</summary>
    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var age = nowUtc - utc;
        if (age < TimeSpan.FromMinutes(1))
            return Strings.Ago_JustNow;
        if (age < TimeSpan.FromHours(1))
            return string.Format(Culture, Strings.Ago_Minutes, (int)age.TotalMinutes);
        if (age < TimeSpan.FromDays(1))
            return string.Format(Culture, Strings.Ago_Hours, (int)age.TotalHours);
        return string.Format(Culture, Strings.Ago_Days, (int)age.TotalDays);
    }

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("d", Culture);

    private static int UnitOf(long bytes)
    {
        var unit = 0;
        double value = Math.Abs(bytes);
        // Switch units where three significant digits would round up to 1000 ("999.6 KB" reads as "1.00 MB").
        while (value >= 999.5 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit;
    }

    private static string Bytes(long bytes, int unit) => $"{Number(bytes, unit)} {Units[unit]}";

    private static string Number(long bytes, int unit)
    {
        if (unit == 0)
            return bytes.ToString(Culture);
        var value = bytes / Math.Pow(1000, unit);
        var format = value >= 99.95 ? "F0" : value >= 9.995 ? "F1" : "F2";
        return value.ToString(format, Culture);
    }
}
