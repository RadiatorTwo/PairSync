using System.Text;
using System.Text.Json.Nodes;
using PairSync.Domain;
using PairSync.Storage.Identity;

namespace PairSync.UnitTests;

public sealed class IdentityBackupTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public void Backup_restores_the_same_key_and_shows_id_and_fingerprint_in_clear()
    {
        using var identity = DeviceIdentity.Create();
        var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        var file = IdentityBackup.Create(identity, Password, now);
        var restored = IdentityBackup.Open(file, Password);

        Assert.Equal(identity.Id, restored.DeviceId);
        Assert.Equal(identity.Fingerprint, restored.Fingerprint);
        Assert.Equal(now, restored.ExportedAtUtc);
        using var copy = DeviceIdentity.FromPrivateKey(restored.DeviceId, restored.PrivateKey);
        Assert.Equal(identity.PublicKey, copy.PublicKey);
        var text = Encoding.UTF8.GetString(file);
        Assert.Contains(identity.Id.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(identity.ExportPrivateKey()), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_password_is_refused()
    {
        using var identity = DeviceIdentity.Create();
        var file = IdentityBackup.Create(identity, Password, DateTime.UtcNow);

        var error = Assert.Throws<InvalidIdentityBackupException>(() => IdentityBackup.Open(file, "wrong password"));
        Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Relabeled_device_id_is_refused()
    {
        using var identity = DeviceIdentity.Create();
        var json = JsonNode.Parse(IdentityBackup.Create(identity, Password, DateTime.UtcNow))!;
        json["deviceId"] = Guid.NewGuid().ToString();

        Assert.Throws<InvalidIdentityBackupException>(() => IdentityBackup.Open(Encoding.UTF8.GetBytes(json.ToJsonString()), Password));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"format":"pairsync-identity","version":1}""")]
    public void Other_files_are_refused(string content) =>
        Assert.Throws<InvalidIdentityBackupException>(() => IdentityBackup.Open(Encoding.UTF8.GetBytes(content), Password));

    [Fact]
    public void Newer_format_asks_for_an_update()
    {
        using var identity = DeviceIdentity.Create();
        var json = JsonNode.Parse(IdentityBackup.Create(identity, Password, DateTime.UtcNow))!;
        json["version"] = 2;

        var error = Assert.Throws<InvalidIdentityBackupException>(() => IdentityBackup.Open(Encoding.UTF8.GetBytes(json.ToJsonString()), Password));
        Assert.Contains("newer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Short_password_is_rejected()
    {
        using var identity = DeviceIdentity.Create();
        Assert.Throws<ArgumentException>(() => IdentityBackup.Create(identity, "short", DateTime.UtcNow));
    }
}
