using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PairSync.Application.Claude;

/// <summary>
/// Recognizes values that look like secrets (plan §9: secrets never leave the device). Errs on the side of caution:
/// a false positive only means the target has to set the value itself.
/// </summary>
public static partial class SecretDetector
{
    /// <summary>Stands in for a removed secret in <c>settings.json</c> values exchanged between devices.</summary>
    public const string Placeholder = "<secret>";

    [GeneratedRegex(@"token|secret|passw|pwd|api[_-]?key|apikey|auth|credential|private[_-]?key|access[_-]?key|bearer|cookie|session",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"^(sk[-_]|ghp_|gho_|ghu_|ghs_|github_pat_|glpat-|xox[abpsr]-|AKIA|ASIA|AIza|ya29\.|npm_|pypi-|hf_|Bearer\s)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretPrefix();

    [GeneratedRegex(@"^[A-Za-z0-9+/=_\-.]{32,}$", RegexOptions.CultureInvariant)]
    private static partial Regex LongToken();

    public static bool IsSecretName(string name) => SecretName().IsMatch(name);

    /// <summary>A known token prefix, or a long unbroken string that mixes letters and digits.</summary>
    public static bool LooksLikeSecretValue(string value)
    {
        if (SecretPrefix().IsMatch(value))
            return true;
        if (value[0] is '/' or '.' or '~' || value.Contains("/.", StringComparison.Ordinal))
            return false; // a path
        return LongToken().IsMatch(value) && value.Any(char.IsDigit) && value.Any(char.IsLetter);
    }

    /// <summary>Whether the value of <paramref name="name"/> must not be sent. References like <c>${VAR}</c> are not secrets.</summary>
    public static bool IsSecret(string name, string? value) =>
        !string.IsNullOrEmpty(value) && !IsReference(value) && value != Placeholder && (IsSecretName(name) || LooksLikeSecretValue(value));

    private static bool IsReference(string value) => value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}');

    /// <summary>A copy of <c>settings.json</c> with secret <c>env</c> values replaced by <see cref="Placeholder"/>.</summary>
    public static JsonObject RedactSettings(JsonObject settings)
    {
        var copy = (JsonObject)settings.DeepClone();
        if (copy["env"] is JsonObject env)
        {
            foreach (var (name, value) in env.ToList())
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var text) && IsSecret(name, text))
                    env[name] = Placeholder;
            }
        }
        return copy;
    }

    /// <summary>
    /// A copy of an MCP server entry in which secret <c>env</c> and <c>headers</c> values are references to an
    /// environment variable of the same name (Claude Code expands <c>${VAR}</c> in MCP configurations).
    /// </summary>
    /// <param name="variables">Receives the names the target has to set.</param>
    public static JsonObject RedactMcpServer(string serverName, JsonObject server, ICollection<string> variables)
    {
        var copy = (JsonObject)server.DeepClone();
        if (copy["env"] is JsonObject env)
        {
            foreach (var (name, value) in env.ToList())
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var text) && IsSecret(name, text))
                {
                    var variable = VariableName(name);
                    env[name] = $"${{{variable}}}";
                    variables.Add(variable);
                }
            }
        }
        if (copy["args"] is JsonArray args)
        {
            // "--token", "abc…" or "--token=abc…" or a bare token.
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i] is not JsonValue v || !v.TryGetValue<string>(out var text))
                    continue;
                var equals = text.IndexOf('=');
                var (prefix, value, flag) = text.StartsWith('-') && equals > 0
                    ? (text[..(equals + 1)], text[(equals + 1)..], text[..equals])
                    : ("", text, i > 0 && args[i - 1] is JsonValue p && p.TryGetValue<string>(out var previous) && previous.StartsWith('-') ? previous : "");
                if (value.StartsWith('-') || IsReference(value) || value.Length == 0)
                    continue;
                if ((flag.Length > 0 && IsSecretName(flag)) || LooksLikeSecretValue(value))
                {
                    var variable = VariableName($"{serverName}_{(flag.Length > 0 ? flag.TrimStart('-') : $"arg{i}")}");
                    args[i] = $"{prefix}${{{variable}}}";
                    variables.Add(variable);
                }
            }
        }
        if (copy["headers"] is JsonObject headers)
        {
            foreach (var (name, value) in headers.ToList())
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var text) && IsSecret(name, text))
                {
                    var variable = VariableName($"{serverName}_{name}");
                    headers[name] = text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? $"Bearer ${{{variable}}}" : $"${{{variable}}}";
                    variables.Add(variable);
                }
            }
        }
        return copy;
    }

    private static string VariableName(string text)
    {
        var name = new string(text.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray());
        return char.IsAsciiDigit(name[0]) ? "_" + name : name;
    }
}
