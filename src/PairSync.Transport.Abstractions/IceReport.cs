namespace PairSync.Transport;

/// <summary>
/// What ICE had to work with: the candidate types each side put into its session description, and the
/// selected pair once connected. After a failed connection this tells whether a public address was known
/// on either side at all.
/// </summary>
public sealed record IceReport(
    IReadOnlyList<CandidateType> LocalCandidates,
    IReadOnlyList<CandidateType> RemoteCandidates,
    RouteInfo? Selected)
{
    public bool LocalHasPublicAddress => HasPublicAddress(LocalCandidates);

    public bool RemoteHasPublicAddress => HasPublicAddress(RemoteCandidates);

    private static bool HasPublicAddress(IReadOnlyList<CandidateType> candidates) =>
        candidates.Any(c => c is CandidateType.ServerReflexive or CandidateType.Relay);
}
