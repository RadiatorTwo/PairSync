using System.Diagnostics;

namespace PairSync.Application.Claude;

public sealed record ClaudeInstallerOptions
{
    /// <summary>The official installer; null picks <c>install.ps1</c> on Windows, <c>install.sh</c> elsewhere.</summary>
    public Uri? ScriptUri { get; init; }

    /// <summary>For tests: serves the script instead of the network.</summary>
    public HttpMessageHandler? Handler { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Null writes the real user PATH (registry or shell files).</summary>
    public IUserPath? UserPath { get; init; }

    /// <summary>Extra environment for the installer process; tests point USERPROFILE/HOME at a temp folder.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public static Uri DefaultScriptUri => OperatingSystem.IsWindows()
        ? new Uri("https://claude.ai/install.ps1")
        : new Uri("https://claude.ai/install.sh");
}

/// <summary>What an installation did, for the UI and the other device.</summary>
public sealed record ClaudeInstallResult(bool Success, string? Version, string Output, PathUpdate? Path, string? Error);

/// <summary>
/// Installs Claude Code with Anthropic's official native installer and puts <c>~/.local/bin</c> on the user's PATH,
/// which the installer leaves to the user. The script is downloaded over HTTPS into a temporary file and run
/// without a shell pipe; it installs for the current user only and asks nothing.
/// </summary>
public sealed class ClaudeInstaller(ClaudeOptions claude, ClaudeInstallerOptions options)
{
    public async Task<ClaudeInstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        var home = claude.HomeDir ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var windows = OperatingSystem.IsWindows();
        var script = Path.Combine(Path.GetTempPath(), $"pairsync-claude-install-{Guid.NewGuid():N}{(windows ? ".ps1" : ".sh")}");
        try
        {
            try
            {
                using var http = options.Handler is null ? new HttpClient() : new HttpClient(options.Handler, disposeHandler: false);
                http.Timeout = TimeSpan.FromMinutes(2);
                var content = await http.GetByteArrayAsync(options.ScriptUri ?? ClaudeInstallerOptions.DefaultScriptUri, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(script, content, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return new ClaudeInstallResult(false, null, "", null, $"The Claude Code installer could not be downloaded: {e.Message}");
            }

            var start = windows
                ? new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script } }
                : new ProcessStartInfo("bash") { ArgumentList = { script } };
            foreach (var (name, value) in options.Environment)
                start.Environment[name] = value;
            var run = await RunAsync(start, cancellationToken).ConfigureAwait(false);
            if (run.TimedOut)
                return new ClaudeInstallResult(false, null, run.Output, null, "The Claude Code installer did not finish in time.");
            if (run.ExitCode != 0)
                return new ClaudeInstallResult(false, null, run.Output, null, $"The Claude Code installer failed (exit code {run.ExitCode}).");

            var bin = Path.Combine(home, ".local", "bin");
            var path = (options.UserPath ?? IUserPath.ForCurrentPlatform(home)).Ensure(bin);
            var program = ClaudeLocator.FindProgram(claude);
            if (program is null)
                return new ClaudeInstallResult(false, null, run.Output, path, $"The installer finished, but no claude program was found in {bin}.");
            var version = await ClaudeLocator.GetVersionAsync(program, cancellationToken).ConfigureAwait(false);
            return new ClaudeInstallResult(true, version, run.Output, path, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new ClaudeInstallResult(false, null, "", null, e.Message);
        }
        finally
        {
            try
            {
                File.Delete(script);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<CliResult> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        // Same process handling as the claude CLI steps: no window, output collected and capped, time limit.
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        return await ClaudeCli.RunProcessAsync(start, options.Timeout, cancellationToken).ConfigureAwait(false);
    }
}
