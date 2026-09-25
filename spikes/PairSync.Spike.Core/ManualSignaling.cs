using System.IO.Compression;
using System.Text;
using PairSync.Transport;

namespace PairSync.Spike;

/// <summary>
/// Manual signaling for the spike (plan §6, "Internet ohne Rendezvous"): the offer and answer travel
/// as compact text codes, either as files in a shared folder or by copy and paste on the console.
/// Signing the codes with the device identity follows in work package 6.
/// </summary>
public sealed class ManualSignaling(string? directory, TextReader input, TextWriter output) : ISignalingProvider
{
    private const string OfferFile = "offer.pairsync";
    private const string AnswerFile = "answer.pairsync";
    private const string OfferPrefix = "PSO1:";
    private const string AnswerPrefix = "PSA1:";

    public Task PublishOfferAsync(SessionDescription offer, CancellationToken cancellationToken)
    {
        if (directory is not null)
            File.Delete(Path.Combine(directory, AnswerFile)); // stale answer from an earlier attempt
        return PublishCodeAsync(OfferFile, Encode(OfferPrefix, offer), cancellationToken);
    }

    public async Task<SessionDescription> ReceiveOfferAsync(CancellationToken cancellationToken) =>
        Decode(await ReceiveCodeAsync(OfferFile, OfferPrefix, cancellationToken).ConfigureAwait(false), OfferPrefix, SessionDescriptionType.Offer);

    public Task PublishAnswerAsync(SessionDescription answer, CancellationToken cancellationToken) =>
        PublishCodeAsync(AnswerFile, Encode(AnswerPrefix, answer), cancellationToken);

    public async Task<SessionDescription> ReceiveAnswerAsync(CancellationToken cancellationToken) =>
        Decode(await ReceiveCodeAsync(AnswerFile, AnswerPrefix, cancellationToken).ConfigureAwait(false), AnswerPrefix, SessionDescriptionType.Answer);

    public static string Encode(string prefix, SessionDescription description)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize))
            deflate.Write(Encoding.UTF8.GetBytes(description.Sdp));
        return prefix + Convert.ToBase64String(buffer.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static SessionDescription Decode(string code, string prefix, SessionDescriptionType type)
    {
        code = code.Trim();
        if (!code.StartsWith(prefix, StringComparison.Ordinal))
            throw new FormatException($"Expected a code starting with {prefix}");
        var base64 = code[prefix.Length..].Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        using var deflate = new DeflateStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        var sdp = reader.ReadToEnd();
        if (sdp.Length > 64 * 1024)
            throw new FormatException("Session description too large.");
        return new SessionDescription(type, sdp);
    }

    /// <summary>Shows a code on the console or writes it to <paramref name="fileName"/> in the signal folder.</summary>
    public async Task PublishCodeAsync(string fileName, string code, CancellationToken cancellationToken)
    {
        if (directory is null)
        {
            await output.WriteLineAsync($"Send this code to the other device:{Environment.NewLine}{code}{Environment.NewLine}").ConfigureAwait(false);
            return;
        }
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var temp = path + ".new";
        await File.WriteAllTextAsync(temp, code, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
        await output.WriteLineAsync($"Wrote {path}").ConfigureAwait(false);
    }

    /// <summary>Reads a code starting with <paramref name="prefix"/> from the console or the signal folder (single use).</summary>
    public async Task<string> ReceiveCodeAsync(string fileName, string prefix, CancellationToken cancellationToken)
    {
        if (directory is null)
        {
            await output.WriteLineAsync($"Paste the code from the other device ({prefix}...):").ConfigureAwait(false);
            while (true)
            {
                var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new OperationCanceledException("Input closed.");
                if (line.Trim().StartsWith(prefix, StringComparison.Ordinal))
                    return line.Trim();
            }
        }

        var path = Path.Combine(directory, fileName);
        await output.WriteLineAsync($"Waiting for {path} ...").ConfigureAwait(false);
        while (!File.Exists(path))
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        var code = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        File.Delete(path); // single use
        return code.Trim();
    }
}
