using Microsoft.Extensions.Logging.Abstractions;
using PairSync.Storage;
using PairSync.Storage.Settings;

namespace PairSync.UnitTests;

public sealed class SettingsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-settings-");

    public void Dispose() => _root.Delete(recursive: true);

    private DataDirectory Data => new(_root.FullName);

    private SettingsStore Open() => new(Data, NullLogger<SettingsStore>.Instance);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var settings = Open().Current;

        Assert.Equal(AppSettings.DefaultPort, settings.Port);
        Assert.Equal(CloseBehavior.Tray, settings.CloseBehavior);
        Assert.Equal(2, settings.ParallelTransfers);
        Assert.Equal(Environment.MachineName, settings.EffectiveDeviceName);
        Assert.False(File.Exists(Data.SettingsPath));
    }

    [Fact]
    public void Update_is_saved_and_reported()
    {
        var store = Open();
        AppSettings? changed = null;
        store.Changed += s => changed = s;

        store.Update(s => s with { DeviceName = "  Studio  ", CloseBehavior = CloseBehavior.Minimize, UploadLimitBytesPerSecond = 1000 });

        var reloaded = Open().Current;
        Assert.Equal("Studio", reloaded.DeviceName);
        Assert.Equal(CloseBehavior.Minimize, reloaded.CloseBehavior);
        Assert.Equal(1000, reloaded.UploadLimitBytesPerSecond);
        Assert.Equal(reloaded, changed);
        Assert.Contains("\"closeBehavior\": \"Minimize\"", File.ReadAllText(Data.SettingsPath));
    }

    [Fact]
    public void Damaged_file_is_set_aside_and_defaults_apply()
    {
        File.WriteAllText(Data.SettingsPath, "{ not json");

        var settings = Open().Current;

        Assert.Equal(new AppSettings(), settings);
        Assert.False(File.Exists(Data.SettingsPath));
        Assert.Equal("{ not json", File.ReadAllText(Data.SettingsPath + ".bad"));
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        File.WriteAllText(Data.SettingsPath, """{ "port": 80, "parallelTransfers": 99, "uploadLimitBytesPerSecond": -5, "closeBehavior": "Tray", "deviceName": "   " }""");

        var settings = Open().Current;

        Assert.Equal(AppSettings.DefaultPort, settings.Port);
        Assert.Equal(8, settings.ParallelTransfers);
        Assert.Equal(0, settings.UploadLimitBytesPerSecond);
        Assert.Null(settings.DeviceName);
    }

    [Fact]
    public void Long_device_name_is_cut()
    {
        var settings = Open().Update(s => s with { DeviceName = new string('x', 200) });
        Assert.Equal(AppSettings.MaxDeviceNameLength, settings.DeviceName!.Length);
    }

    [Fact]
    public void Data_directory_keeps_everything_under_its_root()
    {
        var data = Data;
        foreach (var path in new[] { data.DatabasePath, data.IdentityKeyPath, data.SettingsPath, data.LogsDirectory })
            Assert.Equal(data.Root, Path.GetDirectoryName(path));
        Assert.EndsWith(OperatingSystem.IsWindows() ? "PairSync" : "pairsync", DataDirectory.DefaultRoot());
    }
}
