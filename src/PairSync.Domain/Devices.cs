namespace PairSync.Domain;

/// <summary>Trust state of a paired device. A removed device is deleted and must pair again (plan §11).</summary>
public enum DeviceTrust
{
    Active = 0,
    Blocked = 1,
}

/// <summary>A device this one has paired with. Its public key is the identity; names are for display only.</summary>
public sealed class PairedDevice
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>DER-encoded SubjectPublicKeyInfo of the device key, pinned at pairing time.</summary>
    public byte[] PublicKey { get; set; } = [];

    public DateTime PairedAtUtc { get; set; }

    public DateTime? LastSeenUtc { get; set; }

    public DeviceTrust Trust { get; set; }

    /// <summary>"May send to me": incoming transfers are still confirmed one by one.</summary>
    public bool CanSendToMe { get; set; } = true;
}
