using System.Net;
using System.Security.Cryptography;
using PairSync.Application.Internet;
using PairSync.Application.Pairing;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Stun;
using PairSync.Transport;

namespace PairSync.UnitTests;

/// <summary>
/// Reproducible random tests for everything that parses data from another device (phase 6 block D): random bytes
/// and mutated valid messages may only raise the documented exceptions, never crash or hang.
/// </summary>
public sealed class FuzzTests
{
    private const int Rounds = 3000;
    private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Flips, inserts, deletes or overwrites a few bytes, or truncates.</summary>
    private static byte[] Mutate(Random random, byte[] input)
    {
        var bytes = new List<byte>(input);
        var edits = random.Next(1, 6);
        for (var i = 0; i < edits; i++)
        {
            var at = bytes.Count == 0 ? 0 : random.Next(bytes.Count);
            switch (random.Next(5))
            {
                case 0 when bytes.Count > 0:
                    bytes[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    bytes.Insert(at, (byte)random.Next(256));
                    break;
                case 2 when bytes.Count > 0:
                    bytes.RemoveAt(at);
                    break;
                case 3 when bytes.Count > 0:
                    bytes[at] = (byte)(random.Next(2) == 0 ? 0xFF : random.Next(0xC0, 0xE0)); // MessagePack headers, max values
                    break;
                default:
                    if (bytes.Count > 0)
                        bytes.RemoveRange(at, bytes.Count - at);
                    break;
            }
        }
        return [.. bytes];
    }

    private static byte[] RandomBytes(Random random, int max)
    {
        var bytes = new byte[random.Next(max)];
        random.NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void Control_messages_from_random_and_mutated_bytes_fail_cleanly()
    {
        var random = new Random(4711);
        byte[][] seeds = [.. ControlCodecTests.Messages().Select(m => ControlCodec.Encode(m.Data, Guid.NewGuid()))];
        for (var i = 0; i < Rounds * 3; i++)
        {
            var input = i % 3 == 0 ? RandomBytes(random, 200) : Mutate(random, seeds[random.Next(seeds.Length)]);
            // Keep the major version most of the time, so the body parsers are reached.
            if (input.Length >= 2 && random.Next(4) > 0)
                input[0] = input[1] = 0;
            try
            {
                ControlCodec.Decode(input);
            }
            catch (ProtocolException)
            {
            }
        }
    }

    [Fact]
    public void Data_frames_from_random_bytes_fail_cleanly()
    {
        var random = new Random(815);
        var valid = new byte[4096];
        var length = DataFrameCodec.Write(valid, 42, 3, 0, 2, new byte[32], new byte[100]);
        for (var i = 0; i < Rounds; i++)
        {
            var input = i % 2 == 0 ? RandomBytes(random, 300) : Mutate(random, valid[..length]);
            try
            {
                var frame = DataFrameCodec.Read(input);
                Assert.True(frame.FragmentIndex < frame.FragmentCount);
                Assert.True(frame.ChunkIndex >= 0);
            }
            catch (ProtocolException)
            {
            }
        }
    }

    [Fact]
    public void Codes_and_invitations_from_mutated_text_fail_cleanly()
    {
        var random = new Random(1234);
        using var a = DeviceIdentity.Create();
        using var b = DeviceIdentity.Create();
        var paired = new PairedDevice { Id = a.Id, Name = "a", PublicKey = a.PublicKey };
        var offer = ConnectCodes.CreateOffer(a, b.Id, new SessionDescription(SessionDescriptionType.Offer, ConnectCodeTests.Sdp()),
            RandomNumberGenerator.GetBytes(ConnectCodes.NonceSize), Now.AddMinutes(10), NatHint.Open);
        var answer = ConnectCodes.CreateAnswer(b, "b", RandomNumberGenerator.GetBytes(ConnectCodes.NonceSize),
            new SessionDescription(SessionDescriptionType.Answer, ConnectCodeTests.Sdp("passive")), Now.AddMinutes(10), NatHint.Open);
        var invitation = PairingInvitations.Create(a, "a", [IPAddress.Loopback], 47800, new byte[PairingInvitations.NonceSize], Now.AddMinutes(5),
            new SessionDescription(SessionDescriptionType.Offer, ConnectCodeTests.Sdp()));
        string[] seeds = [offer, answer, invitation];
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_:= \n";

        for (var i = 0; i < Rounds; i++)
        {
            var chars = seeds[i % seeds.Length].ToCharArray();
            for (var edits = random.Next(1, 4); edits > 0; edits--)
                chars[random.Next(5, chars.Length)] = Alphabet[random.Next(Alphabet.Length)];
            var text = random.Next(10) == 0 ? new string(chars, 0, random.Next(chars.Length)) : new string(chars);
            try
            {
                ConnectCodes.ReadOffer(text, Now, TimeSpan.FromMinutes(2), b.Id, id => id == a.Id ? paired : null);
            }
            catch (InvalidConnectCodeException)
            {
            }
            try
            {
                ConnectCodes.ReadAnswer(text, Now, TimeSpan.FromMinutes(2), a.Id);
            }
            catch (InvalidConnectCodeException)
            {
            }
            try
            {
                PairingInvitations.Read(text, Now, TimeSpan.FromMinutes(2), b.Id);
            }
            catch (InvalidInvitationException)
            {
            }
        }
    }

    [Fact]
    public void Compressed_session_descriptions_from_random_bytes_fail_cleanly()
    {
        var random = new Random(99);
        var valid = CodeText.CompressSdp(ConnectCodeTests.Sdp());
        for (var i = 0; i < Rounds; i++)
        {
            try
            {
                var sdp = CodeText.DecompressSdp(i % 2 == 0 ? RandomBytes(random, 400) : Mutate(random, valid));
                Assert.True(sdp.Length <= CodeText.MaxSdpLength);
            }
            catch (FormatException)
            {
            }
        }
    }

    [Fact]
    public void Accepted_relative_paths_always_stay_inside_the_root()
    {
        var random = new Random(2026);
        var root = Path.Combine(Path.GetTempPath(), "pairsync-fuzz-root");
        string[] pieces = ["a", "b", "..", ".", "", "/", "\\", ":", "C:", "~", "con", "NUL.txt", "x ", "y.", "%2e%2e", "\u0000", "‮", "ä", "a/b", "//", " ", "…", "\t"];
        for (var i = 0; i < Rounds * 3; i++)
        {
            var path = string.Concat(Enumerable.Range(0, random.Next(1, 7)).Select(_ => pieces[random.Next(pieces.Length)]));
            if (!RelativePaths.IsValid(path))
            {
                Assert.Throws<ArgumentException>(() => RelativePaths.Combine(root, path));
                continue;
            }
            var full = RelativePaths.Combine(root, path);
            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, full,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            Assert.DoesNotContain("..", path.Split('/'));
        }
    }
}
