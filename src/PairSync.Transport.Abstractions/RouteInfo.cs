namespace PairSync.Transport;

public enum CandidateType { Unknown, Host, ServerReflexive, PeerReflexive, Relay }

/// <summary>The ICE candidate pair a session ended up using. Basis for the route display and NAT diagnostics.</summary>
public sealed record RouteInfo(CandidateType LocalType, string LocalAddress, CandidateType RemoteType, string RemoteAddress)
{
    public bool IsRelayed => LocalType == CandidateType.Relay || RemoteType == CandidateType.Relay;

    public bool IsDirectLan => LocalType == CandidateType.Host && RemoteType == CandidateType.Host;

    public override string ToString() =>
        $"{Describe(LocalType)} {LocalAddress} <-> {Describe(RemoteType)} {RemoteAddress}";

    public static string Describe(CandidateType type) => type switch
    {
        CandidateType.Host => "host",
        CandidateType.ServerReflexive => "srflx",
        CandidateType.PeerReflexive => "prflx",
        CandidateType.Relay => "relay",
        _ => "unknown",
    };
}
