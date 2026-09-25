using System.Net;
using System.Security.Cryptography;
using System.Text;
using PairSync.Application.Internet;
using PairSync.Application.Pairing;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Stun;
using PairSync.Transport;

namespace PairSync.UnitTests;

public sealed class ConnectCodeTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTime Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    private readonly DeviceIdentity _a = DeviceIdentity.Create();
    private readonly DeviceIdentity _b = DeviceIdentity.Create();

    /// <summary>Shaped like a libdatachannel offer: two host candidates per family, a VPN address and srflx.</summary>
    internal static string Sdp(string setup = "active", int candidates = 6)
    {
        var fingerprint = string.Join(':', RandomNumberGenerator.GetBytes(32).Select(b => b.ToString("X2")));
        var sdp = new StringBuilder()
            .Append("v=0\r\no=rtc 2853719447 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0\r\n")
            .Append("a=msid-semantic:WMS *\r\na=ice-options:ice2,trickle\r\n")
            .Append($"a=fingerprint:sha-256 {fingerprint}\r\n")
            .Append("m=application 50412 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 192.168.1.179\r\na=mid:0\r\na=sendrecv\r\n")
            .Append("a=sctp-port:5000\r\na=max-message-size:262144\r\n")
            .Append($"a=setup:{setup}\r\na=ice-ufrag:{Convert.ToHexString(RandomNumberGenerator.GetBytes(2))}\r\n")
            .Append($"a=ice-pwd:{Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=')}\r\n");
        string[] addresses = ["192.168.1.179", "fd00::1c4:2a", "10.64.0.7", "2a02:8108:1a40::9e", "79.227.44.112", "172.28.160.1", "fe80::5c2", "192.168.56.1"];
        for (var i = 0; i < candidates; i++)
        {
            var type = addresses[i % addresses.Length] == "79.227.44.112" ? "srflx raddr 0.0.0.0 rport 0" : "host";
            sdp.Append($"a=candidate:{i + 1} 1 UDP {2122317823 - i * 256} {addresses[i % addresses.Length]} {50412 + i} typ {type}\r\n");
        }
        return sdp.Append("a=end-of-candidates\r\n").ToString();
    }

    private PairedDevice Paired(DeviceIdentity identity, DeviceTrust trust = DeviceTrust.Active) =>
        new() { Id = identity.Id, Name = "office-pc", PublicKey = identity.PublicKey, Trust = trust };

    private string Offer(DateTime? expires = null, byte[]? nonce = null, Guid? to = null, string? sdp = null) =>
        ConnectCodes.CreateOffer(_a, to ?? _b.Id, new SessionDescription(SessionDescriptionType.Offer, sdp ?? Sdp()),
            nonce ?? RandomNumberGenerator.GetBytes(ConnectCodes.NonceSize), expires ?? Now.AddMinutes(10), NatHint.EndpointIndependent);

    private ReceivedOffer ReadOnB(string text, DateTime? now = null, PairedDevice? known = null) =>
        ConnectCodes.ReadOffer(text, now ?? Now, Skew, _b.Id, id => id == _a.Id ? known ?? Paired(_a) : null);

    private string Answer(byte[] offerNonce, DateTime? expires = null) =>
        ConnectCodes.CreateAnswer(_b, "office-pc", offerNonce, new SessionDescription(SessionDescriptionType.Answer, Sdp("passive")),
            expires ?? Now.AddMinutes(10), NatHint.Symmetric);

    [Fact]
    public void Offer_round_trips_through_its_text_form()
    {
        var sdp = Sdp();
        var nonce = RandomNumberGenerator.GetBytes(ConnectCodes.NonceSize);
        var text = Offer(nonce: nonce, sdp: sdp);

        var offer = ReadOnB(text);

        Assert.StartsWith(ConnectCodec.OfferPrefix, text);
        Assert.True(ConnectCodec.LooksLikeOffer(text));
        Assert.Equal(_a.Id, offer.Device.Id);
        Assert.Equal(sdp, offer.Offer.Sdp);
        Assert.Equal(SessionDescriptionType.Offer, offer.Offer.Type);
        Assert.Equal(nonce, offer.Nonce);
        Assert.Equal(NatHint.EndpointIndependent, offer.RemoteNat);
    }

    [Fact]
    public void Codes_stay_small_enough_for_a_readable_qr_code()
    {
        var offer = Offer(sdp: Sdp(candidates: 8));
        var answer = Answer(RandomNumberGenerator.GetBytes(ConnectCodes.NonceSize));

        Assert.True(offer.Length < 2048, $"offer has {offer.Length} characters");
        Assert.True(answer.Length < 2048, $"answer has {answer.Length} characters");
    }

    [Fact]
    public void Whitespace_from_messengers_is_ignored()
    {
        var text = Offer();
        var wrapped = string.Join("\n  ", text.Chunk(40).Select(c => new string(c)));

        Assert.Equal(_a.Id, ReadOnB(wrapped).Device.Id);
    }

    [Fact]
    public void Changed_offer_is_rejected()
    {
        var (envelope, offer) = ConnectCodec.DecodeOffer(Offer());
        var changed = ConnectCodec.SerializePayload(offer with { Sdp = CodeText.CompressSdp(Sdp()) });

        var error = Assert.Throws<InvalidConnectCodeException>(() => ReadOnB(ConnectCodec.EncodeOffer(changed, envelope.Signature!)));
        Assert.Contains("signature", error.Message);
    }

    [Fact]
    public void Offer_signed_by_another_key_is_rejected_even_with_the_right_device_id()
    {
        var (_, offer) = ConnectCodec.DecodeOffer(Offer());
        var attacker = DeviceIdentity.Create();
        var payload = ConnectCodec.SerializePayload(offer);
        var forged = ConnectCodec.EncodeOffer(payload, attacker.Sign(CodeText.SignedData(ConnectCodec.OfferContext, payload)));

        Assert.Throws<InvalidConnectCodeException>(() => ReadOnB(forged));
    }

    [Fact]
    public void Invitation_signature_is_not_accepted_as_connection_code()
    {
        var (_, offer) = ConnectCodec.DecodeOffer(Offer());
        var payload = ConnectCodec.SerializePayload(offer);
        // Same key, but signed for another purpose.
        var wrongContext = ConnectCodec.EncodeOffer(payload, _a.Sign(InvitationCodec.SignedData(payload)));

        Assert.Throws<InvalidConnectCodeException>(() => ReadOnB(wrongContext));
    }

    [Fact]
    public void Offer_for_another_device_is_rejected()
    {
        var error = Assert.Throws<InvalidConnectCodeException>(() => ReadOnB(Offer(to: Guid.NewGuid())));
        Assert.Contains("another device", error.Message);
    }

    [Fact]
    public void Own_offer_is_rejected()
    {
        var text = Offer(to: _a.Id);
        var error = Assert.Throws<InvalidConnectCodeException>(() =>
            ConnectCodes.ReadOffer(text, Now, Skew, _a.Id, _ => Paired(_a)));
        Assert.Contains("this device", error.Message);
    }

    [Fact]
    public void Offer_from_an_unpaired_device_is_rejected()
    {
        var text = Offer();
        var error = Assert.Throws<InvalidConnectCodeException>(() => ConnectCodes.ReadOffer(text, Now, Skew, _b.Id, _ => null));
        Assert.Contains("not paired", error.Message);
    }

    [Fact]
    public void Offer_from_a_blocked_device_is_rejected()
    {
        var error = Assert.Throws<InvalidConnectCodeException>(() => ReadOnB(Offer(), known: Paired(_a, DeviceTrust.Blocked)));
        Assert.Contains("blocked", error.Message);
    }

    [Fact]
    public void Expired_offer_is_rejected_after_the_clock_skew_allowance()
    {
        var text = Offer(expires: Now.AddMinutes(10));

        ReadOnB(text, Now.AddMinutes(11));
        var error = Assert.Throws<InvalidConnectCodeException>(() => ReadOnB(text, Now.AddMinutes(13)));
        Assert.Contains("expired", error.Message);
    }

    [Fact]
    public void Offer_from_an_incompatible_protocol_version_is_rejected()
    {
        var (_, offer) = ConnectCodec.DecodeOffer(Offer());
        var payload = ConnectCodec.SerializePayload(offer with { ProtocolMajor = ProtocolVersion.Major + 1 });
        var text = ConnectCodec.EncodeOffer(payload, _a.Sign(CodeText.SignedData(ConnectCodec.OfferContext, payload)));

        var error = Assert.Throws<InvalidConnectCodeException>(() => ReadOnB(text));
        Assert.Contains("Update PairSync", error.Message);
    }

    [Fact]
    public void Offer_without_dtls_fingerprint_is_rejected()
    {
        var sdp = string.Join("\r\n", Sdp().Split("\r\n").Where(l => !l.StartsWith("a=fingerprint:", StringComparison.Ordinal)));
        Assert.Throws<InvalidConnectCodeException>(() => ReadOnB(Offer(sdp: sdp)));
    }

    [Fact]
    public void Oversized_session_description_is_rejected()
    {
        // Compresses to almost nothing, would expand far beyond the limit.
        var bomb = Sdp() + new string('a', CodeText.MaxSdpLength + 1);
        Assert.Throws<InvalidConnectCodeException>(() => ReadOnB(Offer(sdp: bomb)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("PSC1:")]
    [InlineData("PSC1:!!!!")]
    [InlineData("PSC1:AAAA")]
    [InlineData("PSR1:AAAA")]
    [InlineData("PSI1:AAAA")]
    public void Garbage_is_rejected_with_a_readable_error(string text)
    {
        var error = Assert.Throws<InvalidConnectCodeException>(() => ReadOnB(text));
        Assert.StartsWith("This is not a valid PairSync connection code.", error.Message);
    }

    [Fact]
    public void Too_long_input_is_rejected_before_decoding()
    {
        Assert.Throws<InvalidConnectCodeException>(() => ReadOnB(ConnectCodec.OfferPrefix + new string('A', CodeText.MaxTextLength)));
    }

    [Fact]
    public void Answer_round_trips_and_names_its_device()
    {
        var nonce = RandomNumberGenerator.GetBytes(ConnectCodes.NonceSize);
        var text = Answer(nonce);

        var answer = ConnectCodes.ReadAnswer(text, Now, Skew, _a.Id);

        Assert.StartsWith(ConnectCodec.AnswerPrefix, text);
        Assert.Equal(_b.Id, answer.DeviceId);
        Assert.Equal("office-pc", answer.DeviceName);
        Assert.Equal(_b.PublicKey, answer.PublicKey);
        Assert.Equal(nonce, answer.OfferNonce);
        Assert.Equal(SessionDescriptionType.Answer, answer.Answer.Type);
        Assert.Equal(NatHint.Symmetric, answer.RemoteNat);
        Assert.True(answer.IsFrom(Paired(_b)));
        Assert.False(answer.IsFrom(Paired(DeviceIdentity.Create())));
        Assert.False(answer.IsFrom(new PairedDevice { Id = _b.Id, PublicKey = DeviceIdentity.Create().PublicKey }));
    }

    [Fact]
    public void Changed_answer_is_rejected()
    {
        var (envelope, answer) = ConnectCodec.DecodeAnswer(Answer(RandomNumberGenerator.GetBytes(ConnectCodes.NonceSize)));
        var changed = ConnectCodec.SerializePayload(answer with { OfferNonce = RandomNumberGenerator.GetBytes(ConnectCodes.NonceSize) });

        Assert.Throws<InvalidConnectCodeException>(() =>
            ConnectCodes.ReadAnswer(ConnectCodec.EncodeAnswer(changed, envelope.Signature!), Now, Skew, _a.Id));
    }

    [Fact]
    public void Answer_signed_as_offer_is_rejected()
    {
        var (_, answer) = ConnectCodec.DecodeAnswer(Answer(RandomNumberGenerator.GetBytes(ConnectCodes.NonceSize)));
        var payload = ConnectCodec.SerializePayload(answer);
        var text = ConnectCodec.EncodeAnswer(payload, _b.Sign(CodeText.SignedData(ConnectCodec.OfferContext, payload)));

        Assert.Throws<InvalidConnectCodeException>(() => ConnectCodes.ReadAnswer(text, Now, Skew, _a.Id));
    }

    [Fact]
    public void Expired_and_own_answers_are_rejected()
    {
        var text = Answer(RandomNumberGenerator.GetBytes(ConnectCodes.NonceSize), expires: Now.AddMinutes(10));

        Assert.Contains("expired", Assert.Throws<InvalidConnectCodeException>(() =>
            ConnectCodes.ReadAnswer(text, Now.AddMinutes(13), Skew, _a.Id)).Message);
        Assert.Contains("this device", Assert.Throws<InvalidConnectCodeException>(() =>
            ConnectCodes.ReadAnswer(text, Now, Skew, _b.Id)).Message);
    }

    [Fact]
    public void Each_offer_is_answered_once_until_it_expires()
    {
        var time = new ManualTime { Now = Now };
        var answered = new AnsweredOffers(time);
        var nonce = RandomNumberGenerator.GetBytes(ConnectCodes.NonceSize);

        Assert.True(answered.TryUse(nonce, Now.AddMinutes(10), Skew));
        Assert.False(answered.TryUse(nonce, Now.AddMinutes(10), Skew));
        Assert.True(answered.TryUse(RandomNumberGenerator.GetBytes(ConnectCodes.NonceSize), Now.AddMinutes(10), Skew));

        // Past expiry plus skew the entry is dropped; the code itself would be refused as expired by then.
        time.Now = Now.AddMinutes(13);
        Assert.True(answered.TryUse(nonce, Now.AddMinutes(10), Skew));
    }
}

public sealed class InternetInvitationTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Invitation_with_offer_uses_the_internet_prefix_and_needs_no_lan_address()
    {
        var identity = DeviceIdentity.Create();
        var sdp = ConnectCodeTests.Sdp();
        var text = PairingInvitations.Create(identity, "laptop-win11", [], 0, new byte[PairingInvitations.NonceSize], Now.AddMinutes(5),
            new SessionDescription(SessionDescriptionType.Offer, sdp), NatHint.Symmetric);

        var invitation = PairingInvitations.Read(text, Now, TimeSpan.FromMinutes(2), Guid.NewGuid());

        Assert.StartsWith(InvitationCodec.InternetPrefix, text);
        Assert.True(InvitationCodec.LooksLikeInvitation(text));
        Assert.Equal(sdp, invitation.Offer!.Sdp);
        Assert.Equal(NatHint.Symmetric, invitation.RemoteNat);
        Assert.Empty(invitation.Addresses);
        Assert.True(text.Length < 2048, $"invitation has {text.Length} characters");
    }

    [Fact]
    public void Lan_invitation_keeps_the_old_prefix_and_has_no_offer()
    {
        var text = PairingInvitations.Create(DeviceIdentity.Create(), "laptop-win11", [IPAddress.Parse("192.168.1.20")], 47800,
            new byte[PairingInvitations.NonceSize], Now.AddMinutes(5));

        var invitation = PairingInvitations.Read(text, Now, TimeSpan.FromMinutes(2), Guid.NewGuid());

        Assert.StartsWith(InvitationCodec.Prefix, text);
        Assert.Null(invitation.Offer);
    }

    [Fact]
    public void Swapped_prefix_is_rejected()
    {
        var text = PairingInvitations.Create(DeviceIdentity.Create(), "laptop-win11", [], 0, new byte[PairingInvitations.NonceSize],
            Now.AddMinutes(5), new SessionDescription(SessionDescriptionType.Offer, ConnectCodeTests.Sdp()));
        var relabeled = InvitationCodec.Prefix + text[InvitationCodec.InternetPrefix.Length..];

        Assert.Throws<InvalidInvitationException>(() => PairingInvitations.Read(relabeled, Now, TimeSpan.FromMinutes(2), Guid.NewGuid()));
    }
}
