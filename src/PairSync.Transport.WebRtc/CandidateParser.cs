namespace PairSync.Transport.WebRtc;

/// <summary>Parses ICE candidate lines such as <c>a=candidate:1 1 UDP 2122317823 192.168.1.2 51234 typ host</c>.</summary>
public static class CandidateParser
{
    public static (CandidateType Type, string Address) Parse(string candidate)
    {
        var line = candidate.Trim();
        if (line.StartsWith("a=", StringComparison.Ordinal))
            line = line[2..];
        if (line.StartsWith("candidate:", StringComparison.Ordinal))
            line = line["candidate:".Length..];

        // foundation component transport priority address port "typ" type ...
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var address = parts.Length > 5 ? FormatEndpoint(parts[4], parts[5]) : string.Empty;
        var type = CandidateType.Unknown;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] == "typ")
            {
                type = parts[i + 1] switch
                {
                    "host" => CandidateType.Host,
                    "srflx" => CandidateType.ServerReflexive,
                    "prflx" => CandidateType.PeerReflexive,
                    "relay" => CandidateType.Relay,
                    _ => CandidateType.Unknown,
                };
                break;
            }
        }
        return (type, address);
    }

    private static string FormatEndpoint(string host, string port) =>
        host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
}
