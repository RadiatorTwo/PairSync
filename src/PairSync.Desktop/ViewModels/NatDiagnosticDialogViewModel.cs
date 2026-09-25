using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Stun;

namespace PairSync.Desktop.ViewModels;

/// <summary>One line of the NAT diagnostic: what was checked and what came out.</summary>
public sealed record DiagnosticRow(string Check, string Result, bool IsProblem);

/// <summary>
/// NAT diagnostic (phase 2 block E/F): STUN requests to the configured servers only, then one line per check and a
/// summary in one sentence. The report for copying leaves out the public address unless the user includes it.
/// </summary>
public sealed partial class NatDiagnosticDialogViewModel : DialogViewModel, IDisposable
{
    private readonly IReadOnlyList<string> _servers;
    private readonly IDesktopServices _desktop;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<NatReport>> _run;
    private readonly CancellationTokenSource _closing = new();
    private NatReport? _report;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    private bool _isRunning = true;

    [ObservableProperty]
    private string? _summary;

    [ObservableProperty]
    private string? _publicAddress;

    [ObservableProperty]
    private bool _includePublicAddress;

    [ObservableProperty]
    private string? _copiedText;

    /// <param name="run">The diagnosis; the real one by default, a fake in tests.</param>
    public NatDiagnosticDialogViewModel(IReadOnlyList<string> servers, IDesktopServices desktop,
        Func<IReadOnlyList<string>, CancellationToken, Task<NatReport>>? run = null)
    {
        _servers = servers;
        _desktop = desktop;
        _run = run ?? ((s, ct) => new NatDiagnostics().RunAsync(s, ct));
        Completion = RunAsync();
    }

    public ObservableCollection<DiagnosticRow> Rows { get; } = [];

    public bool IsDone => !IsRunning;

    /// <summary>Completes when the diagnosis has finished (or failed).</summary>
    public Task Completion { get; }

    private async Task RunAsync()
    {
        await Task.Yield();
        if (_servers.Count == 0)
        {
            Summary = Strings.Nat_NoServers;
            IsRunning = false;
            return;
        }
        try
        {
            var report = await _run(_servers, _closing.Token);
            Ui.Run(() => Show(report));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            Ui.Run(() =>
            {
                Summary = string.Format(CultureInfo.CurrentCulture, Strings.Nat_Error, e.Message);
                IsRunning = false;
            });
        }
    }

    internal void Show(NatReport report)
    {
        _report = report;
        Rows.Clear();
        foreach (var row in DescribeChecks(report))
            Rows.Add(row);
        Summary = DescribeSummary(report);
        PublicAddress = report.PublicEndPoint is { } endpoint
            ? string.Format(CultureInfo.CurrentCulture, Strings.Nat_PublicAddress, endpoint)
            : null;
        IsRunning = false;
    }

    public static IEnumerable<DiagnosticRow> DescribeChecks(NatReport report)
    {
        var culture = CultureInfo.CurrentCulture;
        foreach (var server in report.Servers)
        {
            var (result, problem) = server.Status switch
            {
                StunServerStatus.Answered => (Strings.Nat_ServerAnswered, false),
                StunServerStatus.DnsBlocked => (Strings.Nat_ServerDnsBlocked, true),
                StunServerStatus.DnsFailed => (Strings.Nat_ServerDnsFailed, true),
                StunServerStatus.NoResponse => (Strings.Nat_ServerNoResponse, true),
                StunServerStatus.InvalidUri => (Strings.Nat_ServerInvalid, true),
                StunServerStatus.NoUsableAddress => (Strings.Nat_ServerNoAddress, true),
                _ => (Strings.Nat_ServerFailed, true),
            };
            yield return new DiagnosticRow(string.Format(culture, Strings.Nat_CheckServer, server.Server), result, problem);
        }
        yield return new DiagnosticRow(Strings.Nat_CheckDns,
            report.DnsFilterSuspected ? Strings.Nat_DnsFiltered : Strings.Nat_DnsOk, report.DnsFilterSuspected);
        yield return new DiagnosticRow(Strings.Nat_CheckUdp, report.UdpBlocked ? Strings.Nat_UdpBlocked : Strings.Nat_UdpOk, report.UdpBlocked);
        yield return new DiagnosticRow(Strings.Nat_CheckMapping, report.Mapping switch
        {
            NatMappingBehavior.NoNat => Strings.Nat_MappingNone,
            NatMappingBehavior.EndpointIndependent => Strings.Nat_MappingIndependent,
            NatMappingBehavior.EndpointDependent => Strings.Nat_MappingSymmetric,
            _ => Strings.Nat_MappingUnknown,
        }, report.Mapping == NatMappingBehavior.EndpointDependent);
        yield return new DiagnosticRow(Strings.Nat_CheckCgnat, report.CgnatSuspected ? Strings.Nat_CgnatYes : Strings.Nat_CgnatNo, report.CgnatSuspected);
        yield return new DiagnosticRow(Strings.Nat_CheckIpv6,
            !report.Ipv6Checked ? Strings.Nat_Ipv6NotChecked
            : !report.HasGlobalIpv6Address ? Strings.Nat_Ipv6None
            : report.Ipv6StunReachable ? Strings.Nat_Ipv6Ok : Strings.Nat_Ipv6NoStun,
            false);
    }

    public static string DescribeSummary(NatReport report) =>
        report.DnsFilterSuspected && report.Servers.All(s => s.Status != StunServerStatus.Answered) ? Strings.Nat_SummaryDns
        : report.UdpBlocked ? Strings.Nat_SummaryUdp
        : report.Hint switch
        {
            NatHint.Open => Strings.Nat_SummaryOpen,
            NatHint.EndpointIndependent => Strings.Nat_SummaryIndependent,
            NatHint.Symmetric => Strings.Nat_SummarySymmetric,
            _ => Strings.Nat_SummaryUnknown,
        };

    /// <summary>The report as plain text; keys never appear, the public address only if included.</summary>
    public string ReportText()
    {
        var text = new StringBuilder();
        text.AppendLine(Strings.Nat_ReportTitle);
        foreach (var row in Rows)
            text.AppendLine($"{row.Check}: {row.Result}");
        if (Summary is not null)
            text.AppendLine(Summary);
        if (IncludePublicAddress && PublicAddress is not null)
            text.AppendLine(PublicAddress);
        if (_report is { } report)
            text.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:u} · {1:0.0} s", report.StartedAt, report.Duration.TotalSeconds));
        return text.ToString();
    }

    [RelayCommand]
    private async Task CopyReportAsync()
    {
        await _desktop.CopyTextAsync(ReportText());
        CopiedText = Strings.Pair_Copied;
    }

    [RelayCommand]
    private void Done() => Close();

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }
}

/// <summary>"About relays": why some networks need one and that it only ever sees encrypted data.</summary>
public sealed partial class AboutRelaysDialogViewModel : DialogViewModel
{
    [RelayCommand]
    private void Done() => Close();
}
