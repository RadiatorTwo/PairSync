using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace PairSync.Desktop.Platform;

/// <summary>
/// "Start with the desktop session": opens the tray app at login (with <see cref="Program.TrayArgument"/>),
/// not a background service.
/// </summary>
public interface IAutostart
{
    bool IsSupported { get; }

    bool IsEnabled { get; }

    /// <summary>Creates or removes the login entry; creating also refreshes the program path.</summary>
    /// <exception cref="IOException">The entry could not be written.</exception>
    void Set(bool enabled);
}

public static class Autostart
{
    public static IAutostart ForCurrentPlatform()
    {
        var command = LaunchCommand();
        if (OperatingSystem.IsWindows())
            return new WindowsRunKeyAutostart(WindowsRunKeyAutostart.RunKeyPath, "PairSync", command);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
            return new XdgAutostart(XdgAutostart.DefaultConfigHome(), command);
        return new NoAutostart();
    }

    /// <summary>The program plus arguments; <c>dotnet PairSync.dll</c> when started through the host (development).</summary>
    internal static IReadOnlyList<string> LaunchCommand()
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("The program path is unknown.");
        return Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [process, typeof(Autostart).Assembly.Location, Program.TrayArgument]
            : [process, Program.TrayArgument];
    }

    private sealed class NoAutostart : IAutostart
    {
        public bool IsSupported => false;

        public bool IsEnabled => false;

        public void Set(bool enabled)
        {
        }
    }
}

/// <summary>Windows: a value under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRunKeyAutostart(string keyPath, string valueName, IReadOnlyList<string> command) : IAutostart
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsSupported => true;

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is string;
        }
    }

    /// <summary>The value as written: each part quoted.</summary>
    public string CommandLine => string.Join(' ', command.Select(part => '"' + part + '"'));

    public void Set(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
                key.SetValue(valueName, CommandLine, RegistryValueKind.String);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
                key?.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new IOException(e.Message, e);
        }
    }
}

/// <summary>Linux: <c>$XDG_CONFIG_HOME/autostart/pairsync.desktop</c> (freedesktop Desktop Application Autostart).</summary>
public sealed class XdgAutostart(string configHome, IReadOnlyList<string> command) : IAutostart
{
    public string EntryPath { get; } = Path.Combine(configHome, "autostart", "pairsync.desktop");

    public bool IsSupported => true;

    public bool IsEnabled => File.Exists(EntryPath);

    public static string DefaultConfigHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }

    public void Set(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                File.Delete(EntryPath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
            var temp = EntryPath + ".new";
            File.WriteAllText(temp, DesktopEntry(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, EntryPath, overwrite: true);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public string DesktopEntry() =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=PairSync
        Comment=Direct, encrypted transfers between your devices
        Exec={string.Join(' ', command.Select(QuoteExecArgument))}
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """;

    /// <summary>
    /// Quoting rules of the Exec key: double quotes, with <c>" ` $ \</c> escaped by a backslash; <c>%</c> starts a
    /// field code and is doubled.
    /// </summary>
    internal static string QuoteExecArgument(string argument)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in argument)
        {
            if (c is '"' or '`' or '$' or '\\')
                builder.Append('\\');
            else if (c == '%')
                builder.Append('%');
            builder.Append(c);
        }
        // In a desktop file a backslash in a string value is itself escaped, so write each one twice.
        return builder.Append('"').ToString().Replace("\\", "\\\\", StringComparison.Ordinal);
    }
}
