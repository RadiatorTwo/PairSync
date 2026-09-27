using Microsoft.Win32;
using PairSync.Application.Claude;

namespace PairSync.UnitTests.Claude;

public sealed class UserPathTests : IDisposable
{
    private readonly DirectoryInfo _home = Directory.CreateTempSubdirectory("pairsync-userpath-");

    public void Dispose() => _home.Delete(recursive: true);

    private string Bin => Path.Combine(_home.FullName, ".local", "bin");

    [Fact]
    public void Shell_files_get_the_path_once()
    {
        File.WriteAllText(Path.Combine(_home.FullName, ".bashrc"), "alias ll='ls -l'\n");
        Directory.CreateDirectory(Path.Combine(_home.FullName, ".config", "fish"));
        var path = new ShellUserPath(_home.FullName);

        var first = path.Ensure(Bin);
        var second = path.Ensure(Bin);

        Assert.Equal(3, first.Changed.Count);
        Assert.Empty(second.Changed);
        var profile = File.ReadAllText(Path.Combine(_home.FullName, ".profile"));
        Assert.Contains("export PATH=\"$HOME/.local/bin:$PATH\"", profile, StringComparison.Ordinal);
        Assert.StartsWith("alias ll", File.ReadAllText(Path.Combine(_home.FullName, ".bashrc")), StringComparison.Ordinal);
        Assert.Contains("fish_add_path -g \"$HOME/.local/bin\"",
            File.ReadAllText(Path.Combine(_home.FullName, ".config", "fish", "conf.d", "pairsync-path.fish")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_home.FullName, ".zshrc")));
    }

    [Fact]
    public void Shell_file_that_already_mentions_the_folder_is_left_alone()
    {
        var profile = Path.Combine(_home.FullName, ".profile");
        File.WriteAllText(profile, "PATH=\"$HOME/.local/bin:$PATH\"\n");

        var update = new ShellUserPath(_home.FullName).Ensure(Bin);

        Assert.DoesNotContain(profile, update.Changed);
        Assert.Equal("PATH=\"$HOME/.local/bin:$PATH\"\n", File.ReadAllText(profile));
    }

    [Fact]
    public void Windows_user_path_is_appended_once_and_keeps_other_entries()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows registry only");
        if (OperatingSystem.IsWindows())
            WindowsRegistryPath();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WindowsRegistryPath()
    {
        var keyPath = $@"Software\PairSyncTests\{Guid.NewGuid():N}";
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                key.SetValue("Path", @"%USERPROFILE%\bin;C:\Tools", RegistryValueKind.ExpandString);
            var path = new WindowsUserPath(keyPath);
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin-pairsync-test");

            var first = path.Ensure(folder);
            var second = path.Ensure(folder);

            Assert.False(first.AlreadyOnPath);
            Assert.True(second.AlreadyOnPath);
            using var read = Registry.CurrentUser.OpenSubKey(keyPath)!;
            Assert.Equal(RegistryValueKind.ExpandString, read.GetValueKind("Path"));
            Assert.Equal(@"%USERPROFILE%\bin;C:\Tools;%USERPROFILE%\.local\bin-pairsync-test",
                read.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }
}
