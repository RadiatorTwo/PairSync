using System.Net;
using PairSync.Application.Pairing;
using PairSync.Domain;
using PairSync.Protocol;

namespace PairSync.UnitTests;

public sealed class SecurityCodeTests
{
    private static readonly byte[] KeyA = [.. Enumerable.Range(0, 91).Select(i => (byte)i)];
    private static readonly byte[] KeyB = [.. Enumerable.Range(0, 91).Select(i => (byte)(255 - i))];

    [Fact]
    public void Both_devices_derive_the_same_code_regardless_of_key_order()
    {
        var committer = SecurityCode.CreateNonce();
        var responder = SecurityCode.CreateNonce();

        var onA = SecurityCode.Derive(KeyA, KeyB, committer, responder);
        var onB = SecurityCode.Derive(KeyB, KeyA, committer, responder);

        Assert.Equal(onA, onB);
        Assert.Matches(@"^\d{4} \d{4} \d{4}$", onA);
    }

    [Fact]
    public void Code_depends_on_keys_and_on_the_role_of_each_nonce()
    {
        var committer = SecurityCode.CreateNonce();
        var responder = SecurityCode.CreateNonce();
        var code = SecurityCode.Derive(KeyA, KeyB, committer, responder);

        Assert.NotEqual(code, SecurityCode.Derive(KeyA, KeyB, responder, committer));
        Assert.NotEqual(code, SecurityCode.Derive(KeyA, [.. KeyB[..^1], 0], committer, responder));
        Assert.NotEqual(code, SecurityCode.Derive(KeyA, KeyB, committer, SecurityCode.CreateNonce()));
    }

    [Fact]
    public void Code_is_the_first_40_bits_of_the_transcript_hash_as_12_digits()
    {
        var committer = new byte[SecurityCode.NonceSize];
        var responder = Enumerable.Repeat((byte)1, SecurityCode.NonceSize).ToArray();
        byte[] transcript = [0, 0, 0, 91, .. KeyA, 0, 0, 0, 91, .. KeyB, .. committer, .. responder];
        var hash = System.Security.Cryptography.SHA256.HashData(transcript);
        var expected = (((ulong)hash[0] << 32 | (ulong)hash[1] << 24 | (ulong)hash[2] << 16 | (ulong)hash[3] << 8 | hash[4]) % 1_000_000_000_000UL)
            .ToString("D12");

        Assert.Equal($"{expected[..4]} {expected[4..8]} {expected[8..]}", SecurityCode.Derive(KeyB, KeyA, committer, responder));
    }

    [Fact]
    public void Commitment_matches_only_its_nonce()
    {
        var nonce = SecurityCode.CreateNonce();
        var commitment = SecurityCode.Commit(nonce);

        Assert.True(SecurityCode.MatchesCommitment(commitment, nonce));
        Assert.False(SecurityCode.MatchesCommitment(commitment, SecurityCode.CreateNonce()));
        Assert.False(SecurityCode.MatchesCommitment(commitment[..16], nonce));
    }

    [Fact]
    public void Nonces_of_the_wrong_size_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => SecurityCode.Derive(KeyA, KeyB, new byte[16], SecurityCode.CreateNonce()));
    }
}

public sealed class InvitationTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Reader = Guid.NewGuid();

    private static string Create(DeviceIdentity identity, DateTime? expires = null, byte[]? nonce = null) =>
        PairingInvitations.Create(identity, "laptop-win11", [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("fd00::20")], 47800,
            nonce ?? new byte[PairingInvitations.NonceSize], expires ?? Now.AddMinutes(5));

    private static ReceivedInvitation Read(string text, DateTime? now = null) =>
        PairingInvitations.Read(text, now ?? Now, TimeSpan.FromMinutes(2), Reader);

    [Fact]
    public void Invitation_round_trips_through_its_text_form()
    {
        using var identity = DeviceIdentity.Create();
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(PairingInvitations.NonceSize);
        var text = Create(identity, nonce: nonce);

        var invitation = Read(text);

        Assert.StartsWith(InvitationCodec.Prefix, text);
        Assert.Equal(identity.Id, invitation.DeviceId);
        Assert.Equal("laptop-win11", invitation.DeviceName);
        Assert.Equal(identity.PublicKey, invitation.PublicKey);
        Assert.Equal(identity.Fingerprint, invitation.Fingerprint);
        Assert.Equal([IPAddress.Parse("192.168.1.20"), IPAddress.Parse("fd00::20")], invitation.Addresses);
        Assert.Equal(47800, invitation.Port);
        Assert.Equal(nonce, invitation.Nonce);
        Assert.Equal(Now.AddMinutes(5), invitation.ExpiresAtUtc);
    }

    [Fact]
    public void Line_breaks_and_spaces_from_copying_are_ignored()
    {
        using var identity = DeviceIdentity.Create();
        var text = Create(identity);
        var wrapped = "  " + string.Join("\r\n", text.Chunk(40).Select(c => new string(c))) + "\n";

        Assert.Equal(identity.Id, Read(wrapped).DeviceId);
    }

    [Fact]
    public void Any_change_to_the_payload_breaks_the_signature()
    {
        using var identity = DeviceIdentity.Create();
        var (envelope, invitation) = InvitationCodec.Decode(Create(identity));
        var changed = InvitationCodec.SerializePayload(invitation with { Port = 47801 });

        var error = Assert.Throws<InvalidInvitationException>(() => Read(InvitationCodec.Encode(changed, envelope.Signature!)));
        Assert.Contains("signature", error.Message);
    }

    [Fact]
    public void Invitation_signed_by_another_key_is_rejected()
    {
        using var identity = DeviceIdentity.Create();
        using var attacker = DeviceIdentity.Create();
        var (_, invitation) = InvitationCodec.Decode(Create(identity));
        var payload = InvitationCodec.SerializePayload(invitation);

        Assert.Throws<InvalidInvitationException>(() =>
            Read(InvitationCodec.Encode(payload, attacker.Sign(InvitationCodec.SignedData(payload)))));
    }

    [Fact]
    public void Expired_invitation_is_rejected_after_the_clock_tolerance()
    {
        using var identity = DeviceIdentity.Create();
        var text = Create(identity);

        Assert.Equal(identity.Id, Read(text, Now.AddMinutes(6)).DeviceId); // within 2 minutes of tolerance
        var error = Assert.Throws<InvalidInvitationException>(() => Read(text, Now.AddMinutes(8)));
        Assert.Contains("expired", error.Message);
    }

    [Fact]
    public void Own_invitation_is_rejected()
    {
        using var identity = DeviceIdentity.Create();
        var error = Assert.Throws<InvalidInvitationException>(() =>
            PairingInvitations.Read(Create(identity), Now, TimeSpan.Zero, identity.Id));
        Assert.Contains("this device", error.Message);
    }

    [Fact]
    public void Other_major_version_is_rejected_with_an_update_hint()
    {
        using var identity = DeviceIdentity.Create();
        var (_, invitation) = InvitationCodec.Decode(Create(identity));
        var payload = InvitationCodec.SerializePayload(invitation with { ProtocolMajor = ProtocolVersion.Major + 1 });

        var error = Assert.Throws<InvalidInvitationException>(() =>
            Read(InvitationCodec.Encode(payload, identity.Sign(InvitationCodec.SignedData(payload)))));
        Assert.Contains("Update PairSync", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("PSI1:")]
    [InlineData("PSI1:!!!!")]
    [InlineData("PSI1:AAAA")]
    public void Garbage_is_not_an_invitation(string text)
    {
        Assert.Throws<InvalidInvitationException>(() => Read(text));
    }

    [Fact]
    public void Oversized_text_is_rejected_before_decoding()
    {
        Assert.Throws<InvalidInvitationException>(() => Read(InvitationCodec.Prefix + new string('A', InvitationCodec.MaxTextLength)));
    }

    [Fact]
    public void Device_name_from_the_invitation_is_cleaned()
    {
        using var identity = DeviceIdentity.Create();
        var text = PairingInvitations.Create(identity, "evil\u0007\nname" + new string('x', 100), [IPAddress.Loopback], 1,
            new byte[PairingInvitations.NonceSize], Now.AddMinutes(5));

        var name = Read(text).DeviceName;

        Assert.StartsWith("evilname", name);
        Assert.Equal(64, name.Length);
    }
}

public sealed class InvitationBookTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Each_nonce_is_accepted_once()
    {
        var time = new ManualTime();
        var book = new InvitationBook(time);
        var nonce = new byte[] { 1, 2, 3 };
        book.Add(nonce, time.Now.UtcDateTime.AddMinutes(5));

        Assert.True(book.TryRedeem(nonce));
        Assert.False(book.TryRedeem(nonce));
        Assert.False(book.TryRedeem([9, 9, 9]));
    }

    [Fact]
    public void Expired_and_revoked_nonces_are_refused()
    {
        var time = new ManualTime();
        var book = new InvitationBook(time);
        byte[] expiring = [1], revoked = [2];
        book.Add(expiring, time.Now.UtcDateTime.AddMinutes(5));
        book.Add(revoked, time.Now.UtcDateTime.AddMinutes(5));

        book.Revoke(revoked);
        time.Now += TimeSpan.FromMinutes(5.5);

        Assert.False(book.TryRedeem(expiring));
        Assert.False(book.TryRedeem(revoked));
    }
}
