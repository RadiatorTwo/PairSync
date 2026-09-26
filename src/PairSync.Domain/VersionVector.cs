using System.Globalization;
using System.Text;

namespace PairSync.Domain;

/// <summary>How two versions of a path relate.</summary>
public enum VersionOrder
{
    Equal,

    /// <summary>This version contains every change of the other one and more.</summary>
    Newer,

    Older,

    /// <summary>Each side has changes the other one lacks: both changed the same version.</summary>
    Concurrent,
}

/// <summary>
/// A version per path (plan §8 "Konflikte"): a counter per device that changed the path. A device that changes the
/// path raises its own counter. Comparing two vectors tells whether one version follows from the other or whether
/// both were changed independently; clocks play no part.
/// </summary>
public sealed class VersionVector : IEquatable<VersionVector>
{
    public static readonly VersionVector Empty = new(new SortedDictionary<Guid, long>());

    private readonly SortedDictionary<Guid, long> _counters;

    private VersionVector(SortedDictionary<Guid, long> counters) => _counters = counters;

    public IReadOnlyDictionary<Guid, long> Counters => _counters;

    public bool IsEmpty => _counters.Count == 0;

    public long this[Guid device] => _counters.GetValueOrDefault(device);

    public static VersionVector From(IEnumerable<KeyValuePair<Guid, long>> counters)
    {
        var map = new SortedDictionary<Guid, long>();
        foreach (var (device, counter) in counters)
        {
            if (counter < 0)
                throw new ArgumentException("Version counters are never negative.", nameof(counters));
            if (counter > 0)
                map[device] = Math.Max(map.GetValueOrDefault(device), counter);
        }
        return new VersionVector(map);
    }

    /// <summary>The version after <paramref name="device"/> changed the path.</summary>
    public VersionVector Increment(Guid device)
    {
        var map = new SortedDictionary<Guid, long>(_counters) { [device] = this[device] + 1 };
        return new VersionVector(map);
    }

    /// <summary>Contains every change of both versions.</summary>
    public VersionVector Merge(VersionVector other)
    {
        var map = new SortedDictionary<Guid, long>(_counters);
        foreach (var (device, counter) in other._counters)
            map[device] = Math.Max(map.GetValueOrDefault(device), counter);
        return new VersionVector(map);
    }

    public VersionOrder Compare(VersionVector other)
    {
        bool ahead = false, behind = false;
        foreach (var device in _counters.Keys.Union(other._counters.Keys))
        {
            var (mine, theirs) = (this[device], other[device]);
            ahead |= mine > theirs;
            behind |= mine < theirs;
        }
        return (ahead, behind) switch
        {
            (false, false) => VersionOrder.Equal,
            (true, false) => VersionOrder.Newer,
            (false, true) => VersionOrder.Older,
            _ => VersionOrder.Concurrent,
        };
    }

    /// <summary>Text form for storage: <c>deviceId:counter</c> pairs separated by commas, ids without dashes.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (var (device, counter) in _counters)
        {
            if (text.Length > 0)
                text.Append(',');
            text.Append(device.ToString("N")).Append(':').Append(counter.ToString(CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    /// <exception cref="FormatException">Not a vector in text form.</exception>
    public static VersionVector Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Empty;
        var pairs = new List<KeyValuePair<Guid, long>>();
        foreach (var part in text.Split(','))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0 || !Guid.TryParseExact(part[..colon], "N", out var device)
                          || !long.TryParse(part[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var counter))
                throw new FormatException($"'{part}' is not a version counter.");
            pairs.Add(new(device, counter));
        }
        return From(pairs);
    }

    public bool Equals(VersionVector? other) => other is not null && Compare(other) == VersionOrder.Equal;

    public override bool Equals(object? obj) => Equals(obj as VersionVector);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (device, counter) in _counters)
        {
            hash.Add(device);
            hash.Add(counter);
        }
        return hash.ToHashCode();
    }
}
