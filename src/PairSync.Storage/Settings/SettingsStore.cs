using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PairSync.Storage.Settings;

/// <summary>
/// Loads and saves <c>settings.json</c>. A damaged file is set aside as <c>settings.json.bad</c> and the defaults
/// are used, so a bad edit never keeps the app from starting.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly ILogger<SettingsStore> _logger;
    private readonly Lock _gate = new();

    public SettingsStore(DataDirectory dataDirectory, ILogger<SettingsStore> logger)
    {
        _path = dataDirectory.SettingsPath;
        _logger = logger;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    /// <summary>Raised after a successful save, with the new settings.</summary>
    public event Action<AppSettings>? Changed;

    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings updated;
        lock (_gate)
        {
            updated = change(Current).Normalized();
            Write(updated);
            Current = updated;
        }
        Changed?.Invoke(updated);
        return updated;
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path))
            return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllText(_path), SettingsJsonContext.Default.AppSettings);
            return (settings ?? new AppSettings()).Normalized();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Settings file could not be read; using defaults and keeping the old file as settings.json.bad");
            TrySetAside();
            return new AppSettings();
        }
    }

    /// <summary>Writes a sibling file and replaces the settings, so a crash never leaves them half-written.</summary>
    private void Write(AppSettings settings)
    {
        var temp = _path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        File.Move(temp, _path, overwrite: true);
    }

    private void TrySetAside()
    {
        try
        {
            File.Move(_path, _path + ".bad", overwrite: true);
        }
        catch (IOException)
        {
            // best effort; the next save overwrites the damaged file anyway
        }
    }
}
