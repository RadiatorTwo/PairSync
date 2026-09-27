using System.Diagnostics;

namespace PairSync.Application.Claude;

public sealed record ToolInstallerOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>For tests: serves the bun install script instead of the network.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>For tests: replaces the system commands (winget, pkexec + package manager).</summary>
    public Func<string, IReadOnlyList<ToolCommand>>? Commands { get; init; }
}

/// <summary>
/// One program run to install a tool; <see cref="ScriptUri"/> means "download this file (script or package) and pass it
/// as the last argument", saved with <see cref="FileExtension"/> (package managers look at it).
/// </summary>
public sealed record ToolCommand(string FileName, IReadOnlyList<string> Arguments, Uri? ScriptUri = null, string? FileExtension = null);

public sealed record ToolInstallResult(string Tool, bool Success, string Output, string? Error);

/// <summary>
/// Installs git, bun, jq, gh and the Warp terminal for Claude Code and its plugins. Windows: winget (user scope where the package allows it; Git
/// may ask for administrator rights on that screen). Linux: the distribution's package manager through pkexec,
/// which asks for the password on that screen; bun with its official installer into <c>~/.bun</c>. Nothing is
/// installed without the user confirming it on the Claude Code page.
/// </summary>
public sealed class ToolInstaller(ClaudeOptions claude, ToolInstallerOptions options)
{
    private static readonly Dictionary<string, string> WingetIds = new(StringComparer.Ordinal)
    {
        ["git"] = "Git.Git",
        ["bun"] = "Oven-sh.Bun",
        ["jq"] = "jqlang.jq",
        ["gh"] = "GitHub.cli",
        ["warp"] = "Warp.Warp",
    };

    /// <summary>
    /// Installs <paramref name="tools"/> one after the other; each result says whether the tool is found afterwards and
    /// goes to <paramref name="onResult"/> as soon as it is known.
    /// </summary>
    public async Task<IReadOnlyList<ToolInstallResult>> InstallAsync(
        IReadOnlyList<string> tools, Func<ToolInstallResult, Task> onResult, CancellationToken cancellationToken)
    {
        var results = new List<ToolInstallResult>();
        // Linux: one password prompt for all packages instead of one per tool.
        var packages = OperatingSystem.IsWindows() || options.Commands is not null ? [] : tools.Where(t => t is not ("bun" or "warp")).ToList();
        if (tools.Contains("bun") && !OperatingSystem.IsWindows() && options.Commands is null && ClaudeTools.Find("unzip", claude) is null)
            packages.Add("unzip"); // the bun installer unpacks a zip
        string? packageOutput = null, packageError = null;
        if (packages.Count > 0)
            (packageOutput, packageError) = await RunAllAsync(() => PackageManagerCommands(packages), cancellationToken).ConfigureAwait(false);

        foreach (var tool in tools)
        {
            ToolInstallResult result;
            if (!ClaudeTools.IsKnown(tool))
            {
                result = new ToolInstallResult(tool, false, "", $"{tool} is not a tool PairSync installs.");
            }
            else
            {
                var (output, error) = packages.Contains(tool)
                    ? (packageOutput ?? "", packageError)
                    : await RunAllAsync(() => CommandsFor(tool), cancellationToken).ConfigureAwait(false);
                var found = ClaudeTools.Find(tool, claude) is not null;
                result = new ToolInstallResult(tool, found, output, found ? null : error ?? $"{tool} was not found after the installation.");
            }
            results.Add(result);
            await onResult(result).ConfigureAwait(false);
        }
        return results;
    }

    private IReadOnlyList<ToolCommand> CommandsFor(string tool)
    {
        if (options.Commands is { } commands)
            return commands(tool);
        if (OperatingSystem.IsWindows())
        {
            if (ClaudeTools.Find("winget", claude) is { } winget)
                return [new ToolCommand(winget, ["install", "--id", WingetIds[tool], "--exact", "--source", "winget", "--silent",
                    "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"])];
            if (tool == "bun")
                return [new ToolCommand("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"], new Uri("https://bun.sh/install.ps1"))];
            throw new ToolInstallException("winget (App Installer from the Microsoft Store) is missing, so git and jq cannot be installed automatically.");
        }
        if (tool == "bun")
            return [new ToolCommand("bash", [], new Uri("https://bun.sh/install"))];
        if (tool == "warp")
            return WarpCommands();
        return PackageManagerCommands([tool]);
    }

    private IReadOnlyList<ToolCommand> PackageManagerCommands(IReadOnlyList<string> packages)
    {
        var pkexec = ClaudeTools.Find("pkexec", claude)
            ?? throw new ToolInstallException($"pkexec is missing. Install {string.Join(", ", packages)} with your package manager (sudo).");
        string[] prefix = ClaudeTools.Find("pacman", claude) is { } pacman ? [pacman, "-S", "--needed", "--noconfirm"]
            : ClaudeTools.Find("apt-get", claude) is { } apt ? [apt, "install", "-y"]
            : ClaudeTools.Find("dnf", claude) is { } dnf ? [dnf, "install", "-y"]
            : ClaudeTools.Find("zypper", claude) is { } zypper ? [zypper, "--non-interactive", "install"]
            : ClaudeTools.Find("brew", claude) is { } brew ? [brew, "install"]
            : throw new ToolInstallException($"No known package manager found. Install {string.Join(", ", packages)} yourself.");
        var names = PackageNames(prefix[0], packages);
        // Homebrew refuses to run as root.
        return prefix[0].EndsWith("brew", StringComparison.Ordinal)
            ? [new ToolCommand(prefix[0], [.. prefix[1..], .. names])]
            : [new ToolCommand(pkexec, [.. prefix, .. names])];
    }

    /// <summary>Warp is not in the distributions' repositories: its official package from warp.dev, installed as a local file.</summary>
    private IReadOnlyList<ToolCommand> WarpCommands()
    {
        var pkexec = ClaudeTools.Find("pkexec", claude) ?? throw new ToolInstallException("pkexec is missing. Install Warp from warp.dev yourself.");
        return ClaudeTools.Find("pacman", claude) is { } pacman
            ? [new ToolCommand(pkexec, [pacman, "-U", "--noconfirm"], new Uri("https://app.warp.dev/download?package=pacman"), ".pkg.tar.zst")]
            : ClaudeTools.Find("apt-get", claude) is { } apt
                ? [new ToolCommand(pkexec, [apt, "install", "-y"], new Uri("https://app.warp.dev/download?package=deb"), ".deb")]
                : ClaudeTools.Find("dnf", claude) is { } dnf
                    ? [new ToolCommand(pkexec, [dnf, "install", "-y"], new Uri("https://app.warp.dev/download?package=rpm"), ".rpm")]
                    : ClaudeTools.Find("zypper", claude) is { } zypper
                        ? [new ToolCommand(pkexec, [zypper, "--non-interactive", "--no-gpg-checks", "install"], new Uri("https://app.warp.dev/download?package=rpm"), ".rpm")]
                        : throw new ToolInstallException("No known package manager found. Install Warp from warp.dev yourself.");
    }

    /// <summary>Package names for <paramref name="packageManager"/>: Arch calls the GitHub CLI github-cli, everyone else gh.</summary>
    internal static string[] PackageNames(string packageManager, IEnumerable<string> tools) =>
        [.. tools.Select(t => t == "gh" && Path.GetFileName(packageManager) == "pacman" ? "github-cli" : t)];

    private async Task<(string Output, string? Error)> RunAllAsync(Func<IReadOnlyList<ToolCommand>> commands, CancellationToken cancellationToken)
    {
        var output = new System.Text.StringBuilder();
        try
        {
            foreach (var command in commands())
            {
                var result = await RunAsync(command, cancellationToken).ConfigureAwait(false);
                output.Append(result.Output);
                if (result.TimedOut)
                    return (output.ToString(), $"{Path.GetFileName(command.FileName)} did not finish in time.");
                if (result.ExitCode != 0)
                    return (output.ToString(), $"{Path.GetFileName(command.FileName)} failed (exit code {result.ExitCode}).");
            }
            return (output.ToString(), null);
        }
        catch (ToolInstallException e)
        {
            return (output.ToString(), e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException
                                      or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (output.ToString(), e.Message);
        }
    }

    private async Task<CliResult> RunAsync(ToolCommand command, CancellationToken cancellationToken)
    {
        string? script = null;
        try
        {
            var start = new ProcessStartInfo(command.FileName)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var argument in command.Arguments)
                start.ArgumentList.Add(argument);
            if (command.ScriptUri is { } uri)
            {
                script = Path.Combine(Path.GetTempPath(),
                    $"pairsync-tool-{Guid.NewGuid():N}{command.FileExtension ?? (OperatingSystem.IsWindows() ? ".ps1" : ".sh")}");
                using var http = options.Handler is null ? new HttpClient() : new HttpClient(options.Handler, disposeHandler: false);
                http.Timeout = TimeSpan.FromMinutes(10); // Warp's package is about 100 MB
                await File.WriteAllBytesAsync(script, await http.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false), cancellationToken)
                    .ConfigureAwait(false);
                start.ArgumentList.Add(script);
            }
            start.Environment["PATH"] = ClaudeTools.SearchPath(claude);
            return await ClaudeCli.RunProcessAsync(start, options.Timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (script is not null)
                File.Delete(script);
        }
    }

    private sealed class ToolInstallException(string message) : Exception(message);
}
