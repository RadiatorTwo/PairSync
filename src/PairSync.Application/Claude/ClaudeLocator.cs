using System.Diagnostics;

namespace PairSync.Application.Claude;

/// <summary>Where Claude Code lives on this device. Null values are found the usual way; tests set them to a temp folder.</summary>
public sealed record ClaudeOptions
{
    public string? ConfigDir { get; init; }

    public string? GlobalConfigFile { get; init; }

    public string? HomeDir { get; init; }

    /// <summary>The <c>claude</c> program; "" means "not installed".</summary>
    public string? Executable { get; init; }

    /// <summary>Limit for one CLI step on the target (plugin install, MCP add).</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>The configuration folder and the global config file of Claude Code on this device.</summary>
/// <param name="ConfigDirFromEnv">True if <c>CLAUDE_CONFIG_DIR</c> chose <paramref name="ConfigDir"/>.</param>
public sealed record ClaudeEnvironment(string ConfigDir, bool ConfigDirFromEnv, string GlobalConfigFile, string HomeDir);

/// <summary>How to start the <c>claude</c> CLI without a shell: the program plus arguments that go first.</summary>
public sealed record ClaudeProgram(string FileName, IReadOnlyList<string> PrefixArguments);

public static class ClaudeLocator
{
    public static ClaudeEnvironment Locate(ClaudeOptions options)
    {
        var home = options.HomeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fromEnv = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var useEnv = options.ConfigDir is null && !string.IsNullOrWhiteSpace(fromEnv);
        var configDir = Path.GetFullPath(options.ConfigDir ?? (useEnv ? fromEnv! : Path.Combine(home, ".claude")));
        // With CLAUDE_CONFIG_DIR, Claude Code keeps .claude.json inside that folder instead of the home folder.
        var global = options.GlobalConfigFile
                     ?? (useEnv ? Path.Combine(configDir, ".claude.json") : Path.Combine(home, ".claude.json"));
        return new ClaudeEnvironment(configDir, useEnv, global, home);
    }

    /// <summary>Finds the CLI in PATH and in the installers' usual places; null if Claude Code is not installed.</summary>
    public static ClaudeProgram? FindProgram(ClaudeOptions options)
    {
        if (options.Executable is { } configured)
            return configured.Length == 0 ? null : ProgramFor(configured);

        var home = options.HomeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Path.Combine(home, ".local", "bin"))
            .Append(Path.Combine(home, ".claude", "local"));
        string[] names = OperatingSystem.IsWindows() ? ["claude.exe", "claude.cmd"] : ["claude"];
        foreach (var name in names)
        {
            foreach (var folder in folders)
            {
                try
                {
                    var candidate = Path.Combine(folder, name);
                    if (File.Exists(candidate))
                        return ProgramFor(candidate);
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry.
                }
            }
        }
        return null;
    }

    /// <summary>
    /// An npm install on Windows only has <c>claude.cmd</c>; starting it would pass arguments through cmd.exe, which
    /// cannot quote JSON safely. The script it wraps is started with node directly instead.
    /// </summary>
    private static ClaudeProgram? ProgramFor(string path)
    {
        if (!path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            return new ClaudeProgram(path, []);
        var folder = Path.GetDirectoryName(path)!;
        var script = Path.Combine(folder, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        if (!File.Exists(script))
            return null;
        var node = Path.Combine(folder, "node.exe");
        return new ClaudeProgram(File.Exists(node) ? node : "node", [script]);
    }

    /// <summary>"2.1.283" from <c>claude --version</c>, or null if the CLI does not answer.</summary>
    public static async Task<string?> GetVersionAsync(ClaudeProgram program, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ClaudeCli.RunAsync(program, ["--version"], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return null;
            var text = result.Output.Trim();
            var space = text.IndexOf(' ');
            return space > 0 ? text[..space] : text.Length is > 0 and < 64 ? text : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

public sealed record CliResult(int ExitCode, string Output, bool TimedOut);

/// <summary>Runs the <c>claude</c> CLI with an argument list (no shell) and a time limit.</summary>
public static class ClaudeCli
{
    /// <summary>Output kept per step for the log and the UI.</summary>
    public const int MaxOutputChars = 64 * 1024;

    public static async Task<CliResult> RunAsync(ClaudeProgram program, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in program.PrefixArguments.Concat(arguments))
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The claude program did not start.");
        process.StandardInput.Close();
        var output = new System.Text.StringBuilder();
        void Append(string? line)
        {
            if (line is null)
                return;
            lock (output)
            {
                if (output.Length < MaxOutputChars)
                    output.AppendLine(line);
            }
        }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (output)
                return new CliResult(-1, output.ToString(), TimedOut: true);
        }
        // WaitForExitAsync returns after the redirected streams reached their end.
        lock (output)
        {
            var text = output.ToString();
            return new CliResult(process.ExitCode, text.Length > MaxOutputChars ? text[..MaxOutputChars] : text, false);
        }
    }
}
