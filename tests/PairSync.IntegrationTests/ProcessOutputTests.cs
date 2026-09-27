using System.Diagnostics;
using PairSync.Application.Claude;

namespace PairSync.IntegrationTests;

/// <summary>Umlauts in the output of real child processes (installer, winget, claude) as the Claude Code page shows it.</summary>
public sealed class ProcessOutputTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<CliResult> PowerShellAsync(string command)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command },
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        return ClaudeCli.RunProcessAsync(start, TimeSpan.FromSeconds(60), Ct);
    }

    [Theory]
    [InlineData("[Text.Encoding]::UTF8")]
    [InlineData("[Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)")]
    public async Task Bytes_in_either_encoding_arrive_as_umlauts(string encoding)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PowerShell");

        var result = await PowerShellAsync(
            $"$b = ({encoding}).GetBytes(\"Größe geprüft`n\"); $o = [Console]::OpenStandardOutput(); $o.Write($b, 0, $b.Length); $o.Flush()");

        Assert.Equal("Größe geprüft", result.Output.Trim());
    }

    [Fact]
    public async Task Plain_powershell_output_keeps_umlauts()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PowerShell");

        var result = await PowerShellAsync("Write-Output 'Installation abgeschlossen: Grüße'; Write-Error 'Fehler: Datei für Übung fehlt'");

        Assert.Contains("Installation abgeschlossen: Grüße", result.Output, StringComparison.Ordinal);
        Assert.Contains("Datei für Übung fehlt", result.Output, StringComparison.Ordinal);
    }
}
