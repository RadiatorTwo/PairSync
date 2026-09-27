using System.Globalization;
using System.Resources;
using PairSync.Desktop.Resources;

namespace PairSync.UiTests;

/// <summary>The UI texts themselves: no text saved with the wrong encoding ("GerÃ¤t" instead of "Gerät").</summary>
public sealed class TranslationTests
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");

    /// <summary>What UTF-8 text looks like after being read as Windows-1252 once too often.</summary>
    private static readonly string[] Mojibake = ["Ã", "â€", "Â"];

    [Fact]
    public void No_text_in_any_language_is_double_encoded()
    {
        var problems = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, German })
        {
            var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!;
            foreach (System.Collections.DictionaryEntry entry in set)
            {
                if (entry.Value is string text && Mojibake.Any(m => text.Contains(m, StringComparison.Ordinal)))
                    problems.Add($"{culture.Name}/{entry.Key}: {text}");
            }
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void German_umlauts_and_ellipses_arrive_intact()
    {
        Assert.Contains("Gerät", Strings.ResourceManager.GetString(nameof(Strings.Claude_LocalMissing), German), StringComparison.Ordinal);
        Assert.Contains("für", Strings.ResourceManager.GetString(nameof(Strings.Claude_ToolsMissing), German), StringComparison.Ordinal);
        Assert.EndsWith("…", Strings.ResourceManager.GetString(nameof(Strings.Identity_Export), German), StringComparison.Ordinal);
        Assert.EndsWith("…", Strings.ResourceManager.GetString(nameof(Strings.Identity_Export), CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
