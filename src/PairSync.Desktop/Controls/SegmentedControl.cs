using Avalonia.Controls;

namespace PairSync.Desktop.Controls;

/// <summary>
/// Equal-width segments in a Frame border; the selected one is filled with Accent. Disabled items (e.g. offline
/// devices in "Send to") stay visible at 45 %.
/// </summary>
public sealed class SegmentedControl : ListBox;
