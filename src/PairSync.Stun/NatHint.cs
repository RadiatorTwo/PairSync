namespace PairSync.Stun;

/// <summary>
/// Compact NAT classification that travels inside connection codes. The numeric values are part of the code
/// format and must not change.
/// </summary>
public enum NatHint : byte
{
    Unknown = 0,

    /// <summary>No NAT: the mapped address is one of the local interface addresses.</summary>
    Open = 1,

    /// <summary>Same public address and port towards different servers; hole punching usually works.</summary>
    EndpointIndependent = 2,

    /// <summary>A different public mapping per destination (symmetric NAT, often CGNAT).</summary>
    Symmetric = 3,

    /// <summary>No STUN server answered at all.</summary>
    UdpBlocked = 4,
}

/// <summary>How the NAT maps one local socket to public endpoints (RFC 4787 terms).</summary>
public enum NatMappingBehavior
{
    /// <summary>Fewer than two servers answered, so nothing could be compared.</summary>
    Unknown,
    NoNat,
    EndpointIndependent,

    /// <summary>Address- or address-and-port-dependent mapping, i.e. symmetric NAT.</summary>
    EndpointDependent,
}
