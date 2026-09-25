namespace PairSync.Transport;

public enum SessionDescriptionType { Offer, Answer }

/// <summary>A complete (non-trickle) session description including all gathered ICE candidates.</summary>
public sealed record SessionDescription(SessionDescriptionType Type, string Sdp);
