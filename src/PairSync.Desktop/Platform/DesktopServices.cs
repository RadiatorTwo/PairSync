using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace PairSync.Desktop.Platform;

/// <summary>What view models need from the window: pickers, clipboard, opening folders. Tests replace it.</summary>
public interface IDesktopServices
{
    Task<IReadOnlyList<string>> PickFilesAsync();

    Task<string?> PickFolderAsync(string? startIn = null);

    /// <summary>Asks where to save; returns the chosen path or null.</summary>
    Task<string?> PickSaveFileAsync(string suggestedName, string extension);

    /// <summary>Asks for one file with the given extension (e.g. <c>.pairsync-invite</c>).</summary>
    Task<string?> PickOpenFileAsync(string extension);

    Task CopyTextAsync(string text);

    Task OpenFolderAsync(string path);

    /// <summary>Brings the main window to the front (incoming transfer, pairing request).</summary>
    void RevealWindow();
}

/// <summary><see cref="IDesktopServices"/> on the main window.</summary>
internal sealed class WindowDesktopServices(Func<TopLevel?> topLevel, Action reveal) : IDesktopServices
{
    private IStorageProvider? Storage => topLevel()?.StorageProvider;

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        if (Storage is not { } storage)
            return [];
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    public async Task<string?> PickFolderAsync(string? startIn = null)
    {
        if (Storage is not { } storage)
            return null;
        var start = startIn is null ? null : await storage.TryGetFolderFromPathAsync(startIn);
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { SuggestedStartLocation = start });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string extension)
    {
        if (Storage is not { } storage)
            return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType("PairSync") { Patterns = ["*" + extension] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenFileAsync(string extension)
    {
        if (Storage is not { } storage)
            return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            FileTypeFilter = [new FilePickerFileType("PairSync") { Patterns = ["*" + extension] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task CopyTextAsync(string text)
    {
        if (topLevel()?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    public async Task OpenFolderAsync(string path)
    {
        Directory.CreateDirectory(path);
        if (topLevel()?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(Path.GetFullPath(path)));
    }

    public void RevealWindow() => reveal();
}
