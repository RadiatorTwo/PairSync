namespace PairSync.Stun;

/// <summary>ICE candidate types present in one SDP (RFC 8445 §5.1.1).</summary>
[Flags]
public enum CandidateTypes
{
    None = 0,
    Host = 1,
    ServerReflexive = 2,
    PeerReflexive = 4,
    Relay = 8,
}

/// <summary>Why a direct connection most likely failed, for the NAT banner. Ordered roughly by certainty.</summary>
public enum ConnectFailureReason
{
    /// <summary>Nothing conclusive, or a relay was available and it still failed.</summary>
    Unknown,

    /// <summary>Our STUN servers got no UDP response at all.</summary>
    LocalUdpBlocked,

    RemoteUdpBlocked,

    /// <summary>Our offer or answer has no server-reflexive candidate: the STUN servers did not answer or are unreachable.</summary>
    LocalNoPublicAddress,

    RemoteNoPublicAddress,

    /// <summary>Both networks look like symmetric NAT / CGNAT, and no relay is configured.</summary>
    BothSymmetric,

    /// <summary>One side maps each destination to a different port; works only if the other side is open enough.</summary>
    OneSideSymmetric,
}

public static class ConnectFailureAnalysis
{
    /// <summary>
    /// Picks the most likely reason a direct ICE connection failed from both <see cref="NatHint"/>s and the candidate
    /// types of both session descriptions. Pure; no network access.
    /// </summary>
    public static ConnectFailureReason Analyze(
        NatHint localHint, NatHint remoteHint, CandidateTypes localCandidates, CandidateTypes remoteCandidates)
    {
        if ((localCandidates | remoteCandidates).HasFlag(CandidateTypes.Relay))
            return ConnectFailureReason.Unknown;
        if (localHint == NatHint.UdpBlocked)
            return ConnectFailureReason.LocalUdpBlocked;
        if (remoteHint == NatHint.UdpBlocked)
            return ConnectFailureReason.RemoteUdpBlocked;
        if (LacksPublicAddress(localHint, localCandidates))
            return ConnectFailureReason.LocalNoPublicAddress;
        if (LacksPublicAddress(remoteHint, remoteCandidates))
            return ConnectFailureReason.RemoteNoPublicAddress;
        if (localHint == NatHint.Symmetric && remoteHint == NatHint.Symmetric)
            return ConnectFailureReason.BothSymmetric;
        if (localHint == NatHint.Symmetric || remoteHint == NatHint.Symmetric)
            return ConnectFailureReason.OneSideSymmetric;
        return ConnectFailureReason.Unknown;
    }

    /// <summary>
    /// Collects the <c>typ</c> of every <c>a=candidate:</c> line (also bare <c>candidate:</c> lines) in an SDP.
    /// Unknown types and malformed lines are ignored.
    /// </summary>
    public static CandidateTypes CandidateTypesFromSdp(string sdp)
    {
        var types = CandidateTypes.None;
        foreach (var raw in sdp.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            if (line.StartsWith("a=", StringComparison.Ordinal))
                line = line[2..];
            if (!line.StartsWith("candidate:", StringComparison.Ordinal))
                continue;
            var typ = line.IndexOf(" typ ", StringComparison.Ordinal);
            if (typ < 0)
                continue;
            var value = line[(typ + 5)..];
            var end = value.IndexOf(' ');
            if (end >= 0)
                value = value[..end];
            types |= value switch
            {
                "host" => CandidateTypes.Host,
                "srflx" => CandidateTypes.ServerReflexive,
                "prflx" => CandidateTypes.PeerReflexive,
                "relay" => CandidateTypes.Relay,
                _ => CandidateTypes.None,
            };
        }
        return types;
    }

    // Without NAT the host candidate already is the public address; otherwise only srflx carries one.
    private static bool LacksPublicAddress(NatHint hint, CandidateTypes candidates) =>
        hint != NatHint.Open && !candidates.HasFlag(CandidateTypes.ServerReflexive);
}
