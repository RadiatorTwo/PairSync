using PairSync.Stun;

namespace PairSync.UnitTests.Stun;

public sealed class ConnectFailureAnalysisTests
{
    private const CandidateTypes HostAndSrflx = CandidateTypes.Host | CandidateTypes.ServerReflexive;

    [Theory]
    [InlineData(NatHint.Symmetric, NatHint.Symmetric, HostAndSrflx, HostAndSrflx, ConnectFailureReason.BothSymmetric)]
    [InlineData(NatHint.Symmetric, NatHint.EndpointIndependent, HostAndSrflx, HostAndSrflx, ConnectFailureReason.OneSideSymmetric)]
    [InlineData(NatHint.Unknown, NatHint.Symmetric, HostAndSrflx, HostAndSrflx, ConnectFailureReason.OneSideSymmetric)]
    [InlineData(NatHint.UdpBlocked, NatHint.Symmetric, CandidateTypes.Host, HostAndSrflx, ConnectFailureReason.LocalUdpBlocked)]
    [InlineData(NatHint.EndpointIndependent, NatHint.UdpBlocked, HostAndSrflx, CandidateTypes.Host, ConnectFailureReason.RemoteUdpBlocked)]
    [InlineData(NatHint.Unknown, NatHint.EndpointIndependent, CandidateTypes.Host, HostAndSrflx, ConnectFailureReason.LocalNoPublicAddress)]
    [InlineData(NatHint.EndpointIndependent, NatHint.Unknown, HostAndSrflx, CandidateTypes.Host, ConnectFailureReason.RemoteNoPublicAddress)]
    [InlineData(NatHint.Open, NatHint.Symmetric, CandidateTypes.Host, HostAndSrflx, ConnectFailureReason.OneSideSymmetric)]
    [InlineData(NatHint.EndpointIndependent, NatHint.EndpointIndependent, HostAndSrflx, HostAndSrflx, ConnectFailureReason.Unknown)]
    [InlineData(NatHint.Symmetric, NatHint.Symmetric, HostAndSrflx | CandidateTypes.Relay, HostAndSrflx, ConnectFailureReason.Unknown)]
    public void Failure_reason_follows_hints_and_candidates(
        NatHint local, NatHint remote, CandidateTypes localCandidates, CandidateTypes remoteCandidates, ConnectFailureReason expected) =>
        Assert.Equal(expected, ConnectFailureAnalysis.Analyze(local, remote, localCandidates, remoteCandidates));

    [Fact]
    public void Candidate_types_are_read_from_sdp()
    {
        const string sdp = """
            v=0
            o=- 1 1 IN IP4 0.0.0.0
            a=candidate:1 1 UDP 2122317823 192.168.1.20 50000 typ host
            a=candidate:2 1 UDP 1686110207 203.0.113.7 61000 typ srflx raddr 192.168.1.20 rport 50000
            candidate:3 1 UDP 100 198.51.100.1 3478 typ relay raddr 203.0.113.7 rport 61000
            a=candidate:broken
            a=candidate:4 1 UDP 1 10.0.0.1 1 typ unknownkind
            """;

        Assert.Equal(HostAndSrflx | CandidateTypes.Relay, ConnectFailureAnalysis.CandidateTypesFromSdp(sdp));
        Assert.Equal(CandidateTypes.None, ConnectFailureAnalysis.CandidateTypesFromSdp("v=0\r\n"));
    }
}
