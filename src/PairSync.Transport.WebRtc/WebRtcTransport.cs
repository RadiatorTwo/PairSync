namespace PairSync.Transport.WebRtc;

/// <summary>Whether the native WebRTC library could be loaded; without it only the LAN transport works.</summary>
public static class WebRtcTransport
{
    private static readonly Lazy<string?> LoadError = new(() =>
    {
        try
        {
            RtcLogger.EnsureInitialized();
            return null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return e.Message;
        }
    });

    public static bool IsAvailable => LoadError.Value is null;

    /// <summary>Why the library could not be loaded, or null if it is available.</summary>
    public static string? UnavailableReason => LoadError.Value;
}
