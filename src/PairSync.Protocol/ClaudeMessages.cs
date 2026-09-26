using MessagePack;

namespace PairSync.Protocol;

// Claude Code provider (phase 4, protocol 0.6). The source asks for the target's state, the user reviews the plan,
// then the source sends what the target applies. Each exchange is its own short session.

/// <summary>"Show me your Claude Code configuration"; the answer is a <see cref="ClaudeState"/>.</summary>
[MessagePackObject]
public sealed record ClaudeStateRequest : IControlMessage
{
    [Key(0)] public bool Reserved { get; init; }
}

[MessagePackObject]
public sealed record ClaudeFileEntry
{
    /// <summary>Relative to the configuration folder, with <c>/</c>.</summary>
    [Key(0)] public string? Path { get; init; }

    [Key(1)] public long Size { get; init; }

    [Key(2)] public byte[]? Sha256 { get; init; }
}

[MessagePackObject]
public sealed record ClaudePluginEntry
{
    [Key(0)] public string? Id { get; init; }

    [Key(1)] public bool Installed { get; init; }

    [Key(2)] public bool Enabled { get; init; }

    [Key(3)] public string? Version { get; init; }
}

[MessagePackObject]
public sealed record ClaudeMcpEntry
{
    [Key(0)] public string? Name { get; init; }

    /// <summary>The server entry with secrets replaced by variable references and own folders by variables.</summary>
    [Key(1)] public string? Json { get; init; }
}

/// <summary>
/// The target's configuration as far as the source needs it for the comparison. Secrets are removed. Without
/// permission "Apply Claude config" for the asking device only <see cref="Allowed"/> = false is sent.
/// </summary>
[MessagePackObject]
public sealed record ClaudeState : IControlMessage
{
    [Key(0)] public bool Allowed { get; init; }

    [Key(1)] public bool AllowPrograms { get; init; }

    [Key(2)] public string? ConfigDir { get; init; }

    [Key(3)] public bool ConfigDirFromEnv { get; init; }

    /// <summary>Null if the <c>claude</c> CLI was not found.</summary>
    [Key(4)] public string? ClaudeVersion { get; init; }

    [Key(5)] public string? Os { get; init; }

    [Key(6)] public string? HomeDir { get; init; }

    [Key(7)] public ClaudeFileEntry[]? Files { get; init; }

    [Key(8)] public string? SettingsJson { get; init; }

    [Key(9)] public ClaudePluginEntry[]? Plugins { get; init; }

    [Key(10)] public string[]? Marketplaces { get; init; }

    [Key(11)] public ClaudeMcpEntry[]? McpServers { get; init; }

    /// <summary>Names of the target's path variables, e.g. <c>TOOLS_ROOT</c>; the values stay on the target.</summary>
    [Key(12)] public string[]? PathVariables { get; init; }
}

[MessagePackObject]
public sealed record ClaudeSettingEntry
{
    [Key(0)] public string? Key { get; init; }

    /// <summary>A hook event below <c>hooks</c>; null for a whole top-level key.</summary>
    [Key(1)] public string? SubKey { get; init; }

    [Key(2)] public string? Json { get; init; }
}

[MessagePackObject]
public sealed record ClaudeStepEntry
{
    /// <summary>0 marketplace add, 1 plugin install, 2 plugin enable, 3 plugin disable, 4 MCP add.</summary>
    [Key(0)] public int Kind { get; init; }

    [Key(1)] public string? Name { get; init; }

    [Key(2)] public string? Value { get; init; }

    [Key(3)] public string? Title { get; init; }
}

/// <summary>What the target applies. The files follow as <see cref="ClaudeFileData"/> in the order of <see cref="Files"/>.</summary>
[MessagePackObject]
public sealed record ClaudeApply : IControlMessage
{
    [Key(0)] public ClaudeFileEntry[]? Files { get; init; }

    [Key(1)] public ClaudeSettingEntry[]? Settings { get; init; }

    [Key(2)] public ClaudeStepEntry[]? Steps { get; init; }
}

[MessagePackObject]
public sealed record ClaudeFileData : IControlMessage
{
    [Key(0)] public string? Path { get; init; }

    [Key(1)] public long Offset { get; init; }

    [Key(2)] public byte[]? Data { get; init; }

    [Key(3)] public bool Last { get; init; }
}

public enum ClaudeStepStatus
{
    Done = 0,
    Skipped = 1,
    Failed = 2,

    /// <summary>Claude Code is not installed on the target; the step waits there.</summary>
    Pending = 3,
}

[MessagePackObject]
public sealed record ClaudeStepResult : IControlMessage
{
    [Key(0)] public int Index { get; init; }

    [Key(1)] public ClaudeStepStatus Status { get; init; }

    [Key(2)] public int ExitCode { get; init; }

    [Key(3)] public string? Output { get; init; }
}

[MessagePackObject]
public sealed record ClaudeApplyDone : IControlMessage
{
    /// <summary>Null on success; otherwise why nothing (more) was applied.</summary>
    [Key(0)] public string? Error { get; init; }

    [Key(1)] public int FilesWritten { get; init; }

    [Key(2)] public int SettingsWritten { get; init; }

    /// <summary>Notes such as settings left out for lack of permission.</summary>
    [Key(3)] public string[]? Notes { get; init; }

    [Key(4)] public string? BackupFolder { get; init; }
}
