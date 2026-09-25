using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PairSync.Domain;

/// <summary>
/// This installation: a random device id and an ECDSA P-256 key pair (plan §5). The private key never leaves the
/// device; peers pin the public key, so certificates are throwaway wrappers around it.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private readonly ECDsa _key;

    private DeviceIdentity(Guid id, ECDsa key)
    {
        Id = id;
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        Fingerprint = DeviceFingerprint.Of(PublicKey);
    }

    public Guid Id { get; }

    /// <summary>DER-encoded SubjectPublicKeyInfo.</summary>
    public byte[] PublicKey { get; }

    public DeviceFingerprint Fingerprint { get; }

    public static DeviceIdentity Create() => new(Guid.NewGuid(), ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Restores an identity from its private key (PKCS#8).</summary>
    /// <exception cref="CryptographicException">The key is damaged or not a P-256 key.</exception>
    public static DeviceIdentity FromPrivateKey(Guid id, ReadOnlySpan<byte> pkcs8)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out var read);
            if (read != pkcs8.Length || key.KeySize != 256)
                throw new CryptographicException("The device key is not a P-256 key.");
            return new DeviceIdentity(id, key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>The private key as PKCS#8, for the secret store only.</summary>
    public byte[] ExportPrivateKey() => _key.ExportPkcs8PrivateKey();

    public byte[] Sign(ReadOnlySpan<byte> data) => _key.SignData(data, HashAlgorithmName.SHA256);

    public static bool Verify(ReadOnlySpan<byte> subjectPublicKeyInfo, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        }
        catch (CryptographicException)
        {
            return false;
        }
        return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// A self-signed TLS certificate for this key, created fresh at every start. Long validity, because peers pin
    /// the key and ignore dates and names. The caller disposes it.
    /// </summary>
    public X509Certificate2 CreateCertificate(TimeProvider time)
    {
        var request = new CertificateRequest($"CN=PairSync {Id:D}", _key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false)); // server + client auth
        var now = time.GetUtcNow();
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        // Round-trip through PKCS#12 so SChannel on Windows can use the private key.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null);
    }

    /// <summary>Reads the SubjectPublicKeyInfo a peer presented, for pinning against paired devices.</summary>
    public static byte[] PublicKeyOf(X509Certificate2 certificate) => certificate.PublicKey.ExportSubjectPublicKeyInfo();

    public void Dispose() => _key.Dispose();
}
