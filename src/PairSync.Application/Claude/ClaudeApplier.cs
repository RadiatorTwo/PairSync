using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PairSync.Protocol;

namespace PairSync.Application.Claude;

/// <summary>Rejected before anything was written: a path outside the portable entries, a size over the limit, bad JSON.</summary>
public sealed class ClaudeApplyRejectedException(string message) : Exception(message);

/// <summary>
/// The target side of "Apply": checks the request, writes files and <c>settings.json</c> below the configuration
/// folder with a backup of everything it replaces, and runs the CLI steps. Never trusts the source's view of what
/// runs programs: that is decided here from the setting keys and step kinds.
/// </summary>
internal sealed class ClaudeApplier(ClaudeEnvironment environment, string dataRoot, IReadOnlyDictionary<string, string> variables, bool allowPrograms)
{
    public const long MaxTotalSize = 256L * 1024 * 1024;
    public const int MaxSteps = 200;
    public const int KeptBackups = 10;
    private const int WriteBufferSize = 512 * 1024;

    private readonly List<string> _notes = [];
    private string? _backupFolder;

    public IReadOnlyList<string> Notes => _notes;

    public string? BackupFolder => _backupFolder;

    // ---- checking ----

    public static IReadOnlyList<ClaudeFile> CheckFiles(ClaudeFileEntry[]? entries)
    {
        var files = new List<ClaudeFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in entries ?? [])
        {
            if (entry.Path is not { } path || !IsSafeRelativePath(path) || PortableItem.For(path) is null)
                throw new ClaudeApplyRejectedException($"The file {entry.Path} is not part of the portable Claude Code configuration.");
            if (entry.Size is < 0 or > ClaudeInventory.MaxFileSize || entry.Sha256 is not { Length: 32 } || !seen.Add(path))
                throw new ClaudeApplyRejectedException($"The file {path} is too large or listed twice.");
            total += entry.Size;
            files.Add(new ClaudeFile(path, entry.Size, entry.Sha256));
        }
        if (files.Count > ClaudeInventory.MaxFiles || total > MaxTotalSize)
            throw new ClaudeApplyRejectedException("Too many files at once.");
        return files;
    }

    /// <summary>Relative, <c>/</c>-separated, no empty, <c>.</c> or <c>..</c> segments, nothing Windows would misread.</summary>
    public static bool IsSafeRelativePath(string path) =>
        path.Length is > 0 and <= 1024
        && !path.StartsWith('/')
        && path.IndexOfAny(['\\', ':', '\0', '*', '?', '"', '<', '>', '|']) < 0
        && path.Split('/').All(s => s.Length > 0 && s != "." && s != ".." && !s.EndsWith('.') && !s.EndsWith(' ') && !s.Any(char.IsControl));

    public static IReadOnlyList<SettingChange> CheckSettings(ClaudeSettingEntry[]? entries) =>
        [.. (entries ?? []).Select(e =>
        {
            if (e.Key is not { Length: > 0 and <= 128 } key || ClaudePlanner.PluginSettings.Contains(key) || (e.SubKey is not null && key != "hooks"))
                throw new ClaudeApplyRejectedException($"The setting {e.Key} cannot be applied.");
            JsonNode? value;
            try
            {
                value = e.Json is null ? null : JsonNode.Parse(e.Json);
            }
            catch (JsonException)
            {
                value = null;
            }
            if (value is null)
                throw new ClaudeApplyRejectedException($"The value of {key} is not valid JSON.");
            return new SettingChange(key, e.SubKey, value, null);
        })];

    public static IReadOnlyList<ClaudeStep> CheckSteps(ClaudeStepEntry[]? entries)
    {
        if (entries?.Length > MaxSteps)
            throw new ClaudeApplyRejectedException("Too many steps at once.");
        return [.. (entries ?? []).Select(e =>
        {
            var kind = (ClaudeStepKind)e.Kind;
            var name = e.Name ?? "";
            var valid = kind switch
            {
                ClaudeStepKind.MarketplaceAdd => ClaudeInventory.IsName(name) && e.Value is { } source && ClaudePlanner.IsSafeArgument(source),
                ClaudeStepKind.PluginInstall or ClaudeStepKind.PluginEnable or ClaudeStepKind.PluginDisable =>
                    ClaudeInventory.IsPluginId(name) && !name.EndsWith("@synced", StringComparison.Ordinal),
                ClaudeStepKind.McpAdd => ClaudeInventory.IsName(name) && ClaudeWire.ParseObject(e.Value) is not null,
                ClaudeStepKind.InstallClaude => name == "claude-code",
                ClaudeStepKind.InstallTool => ClaudeTools.IsKnown(name),
                _ => false,
            };
            if (!valid)
                throw new ClaudeApplyRejectedException($"The step {e.Title ?? name} cannot be applied.");
            return new ClaudeStep(kind, name, e.Value, null);
        })];
    }

    /// <summary>Whether the step runs programs and needs "Install programs"; decided here, not by the source.</summary>
    public static bool RunsPrograms(ClaudeStep step) => step.Kind switch
    {
        ClaudeStepKind.PluginDisable => false,
        ClaudeStepKind.McpAdd => ClaudePlanner.Classify(ClaudeWire.ParseObject(step.Value)!) != McpKind.Http,
        _ => true,
    };

    // ---- files ----

    /// <summary>Reads the file data that follows <see cref="ClaudeApply"/> into temporary files and checks the hashes.</summary>
    public async Task<IReadOnlyList<string>> ReceiveAsync(IReadOnlyList<ClaudeFile> files, IAsyncEnumerator<IControlMessage> incoming, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(dataRoot, "claude-incoming");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var temps = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var temp = Path.Combine(folder, $"{i}.tmp");
            temps.Add(temp);
            await using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, WriteBufferSize, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            while (true)
            {
                if (!await incoming.MoveNextAsync().ConfigureAwait(false))
                    throw new ClaudeApplyRejectedException("The other device stopped sending.");
                if (incoming.Current is not ClaudeFileData data || data.Path != file.Path || data.Offset != written)
                    throw new ClaudeApplyRejectedException($"Unexpected data for {file.Path}.");
                var bytes = data.Data ?? [];
                written += bytes.Length;
                if (written > file.Size)
                    throw new ClaudeApplyRejectedException($"{file.Path} is larger than announced.");
                hash.AppendData(bytes);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (data.Last)
                    break;
            }
            if (written != file.Size || !hash.GetHashAndReset().AsSpan().SequenceEqual(file.Sha256))
                throw new ClaudeApplyRejectedException($"{file.Path} changed on the other device while it was sent.");
        }
        return temps;
    }

    public int WriteFiles(IReadOnlyList<ClaudeFile> files, IReadOnlyList<string> temps)
    {
        var root = Path.GetFullPath(environment.ConfigDir) + Path.DirectorySeparatorChar;
        for (var i = 0; i < files.Count; i++)
        {
            var target = Path.GetFullPath(Path.Combine(environment.ConfigDir, files[i].Path));
            if (!target.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ClaudeApplyRejectedException($"{files[i].Path} is outside the configuration folder.");
            Backup(target, files[i].Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(temps[i], target, overwrite: true);
        }
        return files.Count;
    }

    private void Backup(string target, string relative)
    {
        if (!File.Exists(target))
            return;
        _backupFolder ??= CreateBackupFolder();
        var copy = Path.Combine(_backupFolder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(target, copy, overwrite: true);
    }

    private string CreateBackupFolder()
    {
        var backups = Path.Combine(dataRoot, "claude-backups");
        foreach (var old in new DirectoryInfo(Directory.CreateDirectory(backups).FullName).GetDirectories()
                     .OrderByDescending(d => d.Name, StringComparer.Ordinal).Skip(KeptBackups - 1))
            old.Delete(recursive: true);
        var folder = Path.Combine(backups, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        return folder;
    }

    // ---- settings.json ----

    public int WriteSettings(IReadOnlyList<SettingChange> changes)
    {
        if (changes.Count == 0)
            return 0;
        var path = Path.Combine(environment.ConfigDir, "settings.json");
        var settings = ClaudeInventory.ReadObject(path) ?? [];
        var written = 0;
        foreach (var change in changes)
        {
            var value = change.Value.DeepClone();
            if (ClaudePlanner.ExecutableSettings.Contains(change.Key))
            {
                if (!allowPrograms)
                {
                    _notes.Add($"{Describe(change)} left out: this device does not allow the other device to install programs.");
                    continue;
                }
                value = ExpandStrings(value);
            }
            if (change.Key == "env" && value is JsonObject env)
            {
                if (settings["env"] is not JsonObject merged)
                    settings["env"] = merged = [];
                foreach (var (name, v) in env)
                {
                    if (v is JsonValue text && text.TryGetValue<string>(out var s) && s == SecretDetector.Placeholder)
                    {
                        if (!merged.ContainsKey(name))
                            _notes.Add($"Set the environment variable {name} on this device; its value was not sent.");
                        continue;
                    }
                    merged[name] = v?.DeepClone();
                }
            }
            else if (change.SubKey is { } hookEvent)
            {
                if (settings["hooks"] is not JsonObject hooks)
                    settings["hooks"] = hooks = [];
                hooks[hookEvent] = value;
            }
            else
            {
                settings[change.Key] = value;
            }
            written++;
        }
        if (written == 0)
            return 0;
        Backup(path, "settings.json");
        Directory.CreateDirectory(environment.ConfigDir);
        var temp = path + ".pairsync.tmp";
        File.WriteAllText(temp, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        File.Move(temp, path, overwrite: true);
        return written;
    }

    private static string Describe(SettingChange change) => change.SubKey is null ? change.Key : $"Hook {change.SubKey}";

    private JsonNode? ExpandStrings(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, ExpandStrings(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(ExpandStrings)]),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(PathMapper.Expand(text, variables)),
        _ => node?.DeepClone(),
    };

    // ---- CLI steps ----

    /// <summary>The CLI arguments for a step; MCP servers get the target's folders and keep secrets it already has.</summary>
    public IReadOnlyList<string> Arguments(ClaudeStep step, JsonObject? existingServer) => step.Kind switch
    {
        ClaudeStepKind.MarketplaceAdd => ["plugin", "marketplace", "add", step.Value!],
        // --yes accepts a marketplace-declared install command; the user confirmed this plugin under "runs programs".
        ClaudeStepKind.PluginInstall => ["plugin", "install", step.Name, "--scope", "user", "--yes"],
        ClaudeStepKind.PluginEnable => ["plugin", "enable", step.Name, "--scope", "user"],
        ClaudeStepKind.PluginDisable => ["plugin", "disable", step.Name, "--scope", "user"],
        _ => ["mcp", "add-json", "--scope", "user", step.Name, McpJson(step, existingServer).ToJsonString()],
    };

    private JsonObject McpJson(ClaudeStep step, JsonObject? existing)
    {
        var server = ClaudeWire.ParseObject(step.Value)!;
        if (server["command"] is JsonValue command && command.TryGetValue<string>(out var text))
            server["command"] = PathMapper.Expand(text, variables);
        if (server["args"] is JsonArray args)
        {
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i] is JsonValue arg && arg.TryGetValue<string>(out var argText))
                    args[i] = PathMapper.Expand(argText, variables);
            }
        }
        // A reference that replaced a secret on the source: keep the value this device already has.
        foreach (var section in new[] { "env", "headers" })
        {
            if (server[section] is not JsonObject values || existing?[section] is not JsonObject old)
                continue;
            foreach (var (name, value) in values.ToList())
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("${", StringComparison.Ordinal)
                    && old[name] is JsonValue o && o.TryGetValue<string>(out var oldValue) && SecretDetector.IsSecret(name, oldValue))
                    values[name] = oldValue;
            }
        }
        return server;
    }

    public static async Task<ClaudeStepResult> RunAsync(ClaudeApplier applier, ClaudeProgram program, ClaudeStep step, int index,
        IReadOnlyDictionary<string, JsonObject> existingServers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            if (step.Kind == ClaudeStepKind.McpAdd && existingServers.ContainsKey(step.Name))
                await ClaudeCli.RunAsync(program, ["mcp", "remove", "--scope", "user", step.Name], timeout, cancellationToken).ConfigureAwait(false);
            var result = await ClaudeCli.RunAsync(program, applier.Arguments(step, existingServers.GetValueOrDefault(step.Name)), timeout, cancellationToken)
                .ConfigureAwait(false);
            return new ClaudeStepResult
            {
                Index = index,
                Status = result.ExitCode == 0 ? ClaudeStepStatus.Done : ClaudeStepStatus.Failed,
                ExitCode = result.ExitCode,
                Output = result.TimedOut ? result.Output + $"\nStopped after {timeout.TotalMinutes:0} minutes." : result.Output,
            };
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ClaudeStepResult { Index = index, Status = ClaudeStepStatus.Failed, ExitCode = -1, Output = e.Message };
        }
    }
}
