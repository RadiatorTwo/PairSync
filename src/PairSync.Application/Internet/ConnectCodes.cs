using PairSync.Application.Pairing;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Stun;
using PairSync.Transport;

namespace PairSync.Application.Internet;

/// <summary>A connection code or answer that cannot be used; the message says why, for the user.</summary>
public sealed class InvalidConnectCodeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A connection code from a paired device that passed all checks.</summary>
public sealed record ReceivedOffer(PairedDevice Device, SessionDescription Offer, byte[] Nonce, DateTime ExpiresAtUtc, NatHint RemoteNat);

/// <summary>
/// An answer code whose signature matches the key it carries. Whether that key is the expected device is up to
/// the caller: <see cref="IsFrom"/> for a paired device, a new pairing for an invitation.
/// </summary>
public sealed record ReceivedAnswer(
    Guid DeviceId, string DeviceName, byte[] PublicKey, byte[] OfferNonce, SessionDescription Answer, DateTime ExpiresAtUtc, NatHint RemoteNat)
{
    public bool IsFrom(PairedDevice device) => device.Id == DeviceId && device.PublicKey.AsSpan().SequenceEqual(PublicKey);
}

/// <summary>A code this device issued: the text for clipboard, file and QR code.</summary>
public sealed record IssuedCode(string Text, DateTime ExpiresAtUtc, byte[] Nonce)
{
    public string SuggestedFileName(string deviceName) =>
        $"{string.Concat(deviceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))}{ConnectCodec.FileExtension}";
}

/// <summary>Creates and checks signed connection codes and answers (plan §6, §11; phase 2 block B).</summary>
public static class ConnectCodes
{
    public const int NonceSize = PairingInvitations.NonceSize;

    public static string CreateOffer(
        DeviceIdentity identity, Guid toDeviceId, SessionDescription offer, byte[] nonce, DateTime expiresAtUtc, NatHint nat)
    {
        if (offer.Type != SessionDescriptionType.Offer)
            throw new ArgumentException("Expected an offer.", nameof(offer));
        var payload = ConnectCodec.SerializePayload(new ConnectOffer
        {
            FromDeviceId = identity.Id,
            ToDeviceId = toDeviceId,
            ProtocolMajor = ProtocolVersion.Major,
            ProtocolMinor = ProtocolVersion.Minor,
            Sdp = CodeText.CompressSdp(offer.Sdp),
            Nonce = nonce,
            ExpiresAtUtc = expiresAtUtc,
            NatHint = (byte)nat,
        });
        return ConnectCodec.EncodeOffer(payload, identity.Sign(CodeText.SignedData(ConnectCodec.OfferContext, payload)));
    }

    /// <summary>Checks a pasted or loaded connection code against the paired devices.</summary>
    /// <param name="findDevice">The paired device with this id, or null.</param>
    /// <param name="clockSkew">Tolerated clock difference; the other device enforces the real expiry.</param>
    /// <exception cref="InvalidConnectCodeException">With a message for the user.</exception>
    public static ReceivedOffer ReadOffer(
        string text, DateTime nowUtc, TimeSpan clockSkew, Guid ownDeviceId, Func<Guid, PairedDevice?> findDevice)
    {
        SignedInvitation envelope;
        ConnectOffer offer;
        try
        {
            (envelope, offer) = ConnectCodec.DecodeOffer(text);
        }
        catch (FormatException e)
        {
            throw new InvalidConnectCodeException($"This is not a valid PairSync connection code. {e.Message}", e);
        }

        CheckVersion(offer.ProtocolMajor, offer.ProtocolMinor, "connection code");
        if (offer.FromDeviceId == ownDeviceId)
            throw new InvalidConnectCodeException("This connection code comes from this device. Open it on the other device.");
        if (offer.ToDeviceId != ownDeviceId)
            throw new InvalidConnectCodeException("This connection code is meant for another device.");
        var device = findDevice(offer.FromDeviceId)
            ?? throw new InvalidConnectCodeException(
                "This connection code comes from a device that is not paired with this one. Pair the devices first.");
        if (!DeviceIdentity.Verify(device.PublicKey, CodeText.SignedData(ConnectCodec.OfferContext, envelope.Payload!), envelope.Signature!))
            throw new InvalidConnectCodeException("The connection code was changed or damaged: its signature does not match.");
        if (device.Trust == DeviceTrust.Blocked)
            throw new InvalidConnectCodeException($"{device.Name} is blocked. Unblock it in Devices to connect.");
        CheckExpiry(offer.ExpiresAtUtc, nowUtc, clockSkew, "connection code", "Create a new one on the other device.");
        var nonce = offer.Nonce is { Length: NonceSize } n ? n : throw new InvalidConnectCodeException("The connection code is incomplete.");

        return new ReceivedOffer(device, new SessionDescription(SessionDescriptionType.Offer, ReadSdp(offer.Sdp, "connection code")),
            nonce, offer.ExpiresAtUtc, ToHint(offer.NatHint));
    }

    public static string CreateAnswer(
        DeviceIdentity identity, string deviceName, byte[] offerNonce, SessionDescription answer, DateTime expiresAtUtc, NatHint nat)
    {
        if (answer.Type != SessionDescriptionType.Answer)
            throw new ArgumentException("Expected an answer.", nameof(answer));
        var payload = ConnectCodec.SerializePayload(new ConnectAnswer
        {
            FromDeviceId = identity.Id,
            DeviceName = deviceName,
            PublicKey = identity.PublicKey,
            ProtocolMajor = ProtocolVersion.Major,
            ProtocolMinor = ProtocolVersion.Minor,
            OfferNonce = offerNonce,
            Sdp = CodeText.CompressSdp(answer.Sdp),
            ExpiresAtUtc = expiresAtUtc,
            NatHint = (byte)nat,
        });
        return ConnectCodec.EncodeAnswer(payload, identity.Sign(CodeText.SignedData(ConnectCodec.AnswerContext, payload)));
    }

    /// <summary>
    /// Checks signature, version, expiry and content of an answer code. The caller still has to match
    /// <see cref="ReceivedAnswer.OfferNonce"/> to one of its open offers and check who answered.
    /// </summary>
    /// <exception cref="InvalidConnectCodeException">With a message for the user.</exception>
    public static ReceivedAnswer ReadAnswer(string text, DateTime nowUtc, TimeSpan clockSkew, Guid ownDeviceId)
    {
        SignedInvitation envelope;
        ConnectAnswer answer;
        try
        {
            (envelope, answer) = ConnectCodec.DecodeAnswer(text);
        }
        catch (FormatException e)
        {
            throw new InvalidConnectCodeException($"This is not a valid PairSync answer code. {e.Message}", e);
        }

        if (answer.PublicKey is not { Length: > 0 } key ||
            !DeviceIdentity.Verify(key, CodeText.SignedData(ConnectCodec.AnswerContext, envelope.Payload!), envelope.Signature!))
            throw new InvalidConnectCodeException("The answer code was changed or damaged: its signature does not match.");
        CheckVersion(answer.ProtocolMajor, answer.ProtocolMinor, "answer code");
        if (answer.FromDeviceId == ownDeviceId)
            throw new InvalidConnectCodeException("This answer code comes from this device. Paste it on the other device.");
        CheckExpiry(answer.ExpiresAtUtc, nowUtc, clockSkew, "answer code", "Start the connection again.");
        // Answers to invitations carry the invitation nonce, which has the same size.
        var offerNonce = answer.OfferNonce is { Length: NonceSize } n
            ? n : throw new InvalidConnectCodeException("The answer code is incomplete.");

        return new ReceivedAnswer(answer.FromDeviceId, DeviceNames.Clean(answer.DeviceName, answer.FromDeviceId), key, offerNonce,
            new SessionDescription(SessionDescriptionType.Answer, ReadSdp(answer.Sdp, "answer code")), answer.ExpiresAtUtc,
            ToHint(answer.NatHint));
    }

    internal static NatHint ToHint(byte value) => Enum.IsDefined((NatHint)value) ? (NatHint)value : NatHint.Unknown;

    internal static string ReadSdp(byte[]? compressed, string what)
    {
        if (compressed is not { Length: > 0 })
            throw new InvalidConnectCodeException($"The {what} is incomplete.");
        string sdp;
        try
        {
            sdp = CodeText.DecompressSdp(compressed);
        }
        catch (FormatException e)
        {
            throw new InvalidConnectCodeException($"The {what} is damaged. {e.Message}", e);
        }
        // Without a fingerprint DTLS could not be bound to the signed code, without a candidate there is nothing to try.
        if (!sdp.Contains("a=fingerprint:", StringComparison.Ordinal) || !sdp.Contains("a=candidate:", StringComparison.Ordinal))
            throw new InvalidConnectCodeException($"The {what} is incomplete.");
        return sdp;
    }

    private static void CheckVersion(ushort major, ushort minor, string what)
    {
        if (major != ProtocolVersion.Major)
            throw new InvalidConnectCodeException(
                $"The {what} comes from protocol version {major}.{minor}, this device uses {ProtocolVersion.Current}. " +
                "Update PairSync on the older device.");
    }

    private static void CheckExpiry(DateTime expiresAtUtc, DateTime nowUtc, TimeSpan clockSkew, string what, string remedy)
    {
        if (nowUtc > expiresAtUtc + clockSkew)
            throw new InvalidConnectCodeException(
                $"The {what} has expired. {remedy} If it was just created, check that the clocks of both devices are correct.");
    }
}

/// <summary>
/// Offers this device already answered. Each connection code is answered once; a copy that shows up again (a second
/// paste, a replay) is refused until it would have expired anyway.
/// </summary>
public sealed class AnsweredOffers(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTime> _used = [];

    /// <returns>False if this nonce was answered before.</returns>
    public bool TryUse(byte[] nonce, DateTime expiresAtUtc, TimeSpan clockSkew)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow().UtcDateTime;
            foreach (var expired in _used.Where(e => e.Value < now).Select(e => e.Key).ToList())
                _used.Remove(expired);
            return _used.TryAdd(Convert.ToHexString(nonce), expiresAtUtc + clockSkew);
        }
    }
}
