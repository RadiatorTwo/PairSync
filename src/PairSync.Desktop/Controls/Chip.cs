using Avalonia.Controls;

namespace PairSync.Desktop.Controls;

/// <summary>
/// Small label for permissions and counts. Default: granted (Accent100 background). Classes: <c>off</c>
/// (not granted: Hairline outline, disabled text), <c>strong</c> (Accent900, e.g. "Blocked").
/// </summary>
public sealed class Chip : ContentControl;
