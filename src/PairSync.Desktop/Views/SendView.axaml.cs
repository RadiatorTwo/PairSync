using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PairSync.Desktop.ViewModels;

namespace PairSync.Desktop.Views;

public sealed partial class SendView : UserControl
{
    public SendView()
    {
        InitializeComponent();
        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not SendViewModel send || e.DataTransfer.TryGetFiles() is not { } items)
            return;
        var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            await send.AddPathsAsync(paths);
    }
}
