using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PairSync.Desktop.Platform;

namespace PairSync.Desktop.ViewModels;

public enum ClaudeLogKind
{
    Info,
    Running,
    Done,
    Skipped,
    Pending,
    Failed,
}

/// <summary>One line of the Claude Code log: time, what, how it went and the program output (selectable, folded unless it failed).</summary>
public sealed partial class ClaudeLogEntryViewModel(DateTime time, string title, ClaudeLogKind kind, string status, string? output)
    : ObservableObject
{
    public string Time { get; } = time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public string Title { get; } = title;

    public ClaudeLogKind Kind { get; } = kind;

    public string Status { get; } = status;

    public string? Output { get; } = output;

    public bool HasOutput => Output is not null;

    public bool HasStatus => Status.Length > 0;

    public bool IsFailed => Kind == ClaudeLogKind.Failed;

    public bool IsDone => Kind == ClaudeLogKind.Done;

    public bool IsMuted => Kind is ClaudeLogKind.Skipped or ClaudeLogKind.Pending or ClaudeLogKind.Info;

    /// <summary>Failed steps show their output right away.</summary>
    [ObservableProperty]
    private bool _isExpanded = kind == ClaudeLogKind.Failed;

    public string Text => $"[{Time}] {Title}{(Status.Length > 0 ? " — " + Status : "")}"
                          + (Output is null ? "" : "\n" + string.Join('\n', Output.Split('\n').Select(l => "    " + l.TrimEnd('\r'))));
}

/// <summary>The log under the Claude Code page, for "Apply" here and for applies another device runs on this one.</summary>
public sealed partial class ClaudeLogViewModel(IDesktopServices? desktop) : ObservableObject
{
    public const int MaxEntries = 500;

    public ObservableCollection<ClaudeLogEntryViewModel> Entries { get; } = [];

    /// <summary>Heading above the entries: "Apply on office-pc" or "From laptop".</summary>
    [ObservableProperty]
    private string _heading = "";

    public bool HasEntries => Entries.Count > 0;

    public void Start(string heading)
    {
        Entries.Clear();
        Heading = heading;
        OnPropertyChanged(nameof(HasEntries));
    }

    public void Add(string title, ClaudeLogKind kind = ClaudeLogKind.Info, string status = "", string? output = null)
    {
        if (Entries.Count >= MaxEntries)
            Entries.RemoveAt(0);
        Entries.Add(new ClaudeLogEntryViewModel(DateTime.UtcNow, title, kind, status, string.IsNullOrWhiteSpace(output) ? null : output.Trim()));
        OnPropertyChanged(nameof(HasEntries));
    }

    public string Text
    {
        get
        {
            var text = new StringBuilder();
            if (Heading.Length > 0)
                text.AppendLine(Heading);
            foreach (var entry in Entries)
                text.AppendLine(entry.Text);
            return text.ToString();
        }
    }

    [RelayCommand]
    private Task CopyAsync() => desktop?.CopyTextAsync(Text) ?? Task.CompletedTask;
}
