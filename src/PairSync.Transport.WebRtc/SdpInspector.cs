namespace PairSync.Transport.WebRtc;

/// <summary>Reads and adjusts the few parts of a session description PairSync cares about.</summary>
public static class SdpInspector
{
    /// <summary>Types of all <c>a=candidate</c> lines, in order.</summary>
    public static IReadOnlyList<CandidateType> CandidateTypes(string sdp) =>
        Lines(sdp)
            .Where(line => line.StartsWith("a=candidate:", StringComparison.Ordinal))
            .Select(line => CandidateParser.Parse(line).Type)
            .ToList();

    /// <summary>
    /// Makes the offering side the DTLS client (<c>a=setup:active</c> instead of <c>actpass</c>), so the
    /// answering side becomes the DTLS server. A DTLS client gives up after ~30 s without a reply, a server
    /// waits for the first ClientHello; with manual signaling the answering side is the one that waits,
    /// because its answer still has to travel back by hand. The offering side accepts either role until
    /// it sees the answer, so only the transmitted copy changes.
    /// </summary>
    internal static string WithActiveSetup(string offerSdp) =>
        offerSdp.Replace("a=setup:actpass", "a=setup:active", StringComparison.Ordinal);

    private static IEnumerable<string> Lines(string sdp) =>
        sdp.Split('\n').Select(line => line.TrimEnd('\r'));
}
