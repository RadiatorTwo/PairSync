using Tmds.DBus.Protocol;

namespace PairSync.Storage.Secrets;

/// <summary>
/// Linux: the freedesktop Secret Service API (GNOME Keyring, KWallet 5.97+, KeePassXC) over the session bus.
/// Items are found by attributes, including the data directory, so two PairSync data directories never share a key.
/// The session uses the "plain" algorithm: the secret travels unencrypted over the session bus, which only
/// processes of the same user can connect to (libsecret treats this as acceptable as well).
/// </summary>
public sealed class SecretServiceStore : ISecretStore
{
    private const string Service = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";
    private const string NoPrompt = "/";

    /// <summary>How long the user gets for an unlock prompt of the keyring.</summary>
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(2);

    private readonly DBusConnection _connection;
    private readonly string _session;
    private readonly string _scope;

    private SecretServiceStore(DBusConnection connection, string session, string scope)
    {
        _connection = connection;
        _session = session;
        _scope = scope;
    }

    public SecretProtection Protection => SecretProtection.SecretService;

    /// <summary>Connects to the Secret Service; null if there is no session bus, no service or no default collection.</summary>
    /// <param name="scope">Distinguishes installations of the same user, normally the data directory.</param>
    public static async Task<SecretServiceStore?> TryOpenAsync(string scope, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() || DBusAddress.Session is not { Length: > 0 } address)
            return null;

        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            var running = await connection.ListServicesAsync().ConfigureAwait(false);
            if (!running.Contains(Service))
            {
                var activatable = await connection.ListActivatableServicesAsync().ConfigureAwait(false);
                if (!activatable.Contains(Service))
                {
                    connection.Dispose();
                    return null;
                }
            }

            var session = await OpenSessionAsync(connection).WaitAsync(cancellationToken).ConfigureAwait(false);
            var store = new SecretServiceStore(connection, session, scope);
            if (await store.ReadDefaultCollectionAsync().WaitAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                store.Dispose();
                return null;
            }
            return store;
        }
        catch (Exception e) when (e is DBusExceptionBase or IOException)
        {
            connection.Dispose();
            return null;
        }
    }

    public async Task<byte[]?> LoadAsync(string name, CancellationToken cancellationToken)
    {
        SecretNames.Validate(name);
        try
        {
            var item = (await FindUnlockedAsync(name, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            return item is null ? null : await GetSecretAsync(item).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DBusExceptionBase or IOException)
        {
            throw new SecretStoreUnavailableException($"The keyring could not be read: {e.Message}", e);
        }
    }

    public async Task SaveAsync(string name, byte[] secret, CancellationToken cancellationToken)
    {
        SecretNames.Validate(name);
        try
        {
            var collection = await ReadDefaultCollectionAsync().WaitAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new SecretStoreUnavailableException("The keyring has no default collection.");
            await UnlockAsync([collection], cancellationToken).ConfigureAwait(false);

            var (_, prompt) = await CreateItemAsync(collection, name, secret).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (prompt != NoPrompt)
                await PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DBusExceptionBase or IOException)
        {
            throw new SecretStoreUnavailableException($"The keyring could not store the secret: {e.Message}", e);
        }
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        SecretNames.Validate(name);
        try
        {
            foreach (var item in await FindUnlockedAsync(name, cancellationToken).ConfigureAwait(false))
            {
                var prompt = await CallAsync(item, ItemInterface, "Delete", "", null, (m, _) => m.GetBodyReader().ReadObjectPathAsString())
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                if (prompt != NoPrompt)
                    await PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is DBusExceptionBase or IOException)
        {
            throw new SecretStoreUnavailableException($"The keyring could not delete the secret: {e.Message}", e);
        }
    }

    public void Dispose() => _connection.Dispose();

    private Dictionary<string, string> Attributes(string name) => new()
    {
        ["application"] = "PairSync",
        ["pairsync-secret"] = name,
        ["pairsync-scope"] = _scope,
    };

    /// <summary>Items with this name, unlocking locked ones (may show the keyring password prompt).</summary>
    private async Task<IReadOnlyList<string>> FindUnlockedAsync(string name, CancellationToken cancellationToken)
    {
        var attributes = Attributes(name);
        var (unlocked, locked) = await CallAsync(ServicePath, ServiceInterface, "SearchItems", "a{ss}",
            writer => WriteStringDictionary(writer, attributes),
            (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (ToStrings(reader.ReadArrayOfObjectPath()), ToStrings(reader.ReadArrayOfObjectPath()));
            }).WaitAsync(cancellationToken).ConfigureAwait(false);

        if (locked.Count == 0)
            return unlocked;
        await UnlockAsync(locked, cancellationToken).ConfigureAwait(false);
        return [.. unlocked, .. locked];
    }

    private async Task UnlockAsync(IReadOnlyList<string> objects, CancellationToken cancellationToken)
    {
        var (_, prompt) = await CallAsync(ServicePath, ServiceInterface, "Unlock", "ao",
            writer => writer.WriteArray(objects.Select(o => new ObjectPath(o)).ToArray()),
            (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (reader.ReadArrayOfObjectPath(), reader.ReadObjectPathAsString());
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (prompt != NoPrompt)
            await PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Shows a keyring prompt and waits for the user; throws if it is dismissed.</summary>
    private async Task PromptAsync(string prompt, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Subscribe before triggering the prompt so the Completed signal cannot be missed.
        using var watch = await _connection.WatchSignalAsync(
            sender: null, path: prompt, @interface: PromptInterface, signal: "Completed",
            reader: (m, _) => m.GetBodyReader().ReadBool(),
            handler: notification =>
            {
                if (notification.HasValue)
                    completed.TrySetResult(notification.Value);
                else if (notification.Exception is { } e)
                    completed.TrySetException(e);
            },
            flags: ObserverFlags.None, emitOnCapturedContext: false, state: null).ConfigureAwait(false);

        await CallAsync(prompt, PromptInterface, "Prompt", "s", writer => writer.WriteString(""), (_, _) => true)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

        bool dismissed;
        try
        {
            dismissed = await completed.Task.WaitAsync(PromptTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new SecretStoreUnavailableException("The keyring prompt was not answered.", e);
        }
        if (dismissed)
            throw new SecretStoreUnavailableException("The keyring prompt was dismissed.");
    }

    private async Task<string?> ReadDefaultCollectionAsync()
    {
        var path = await CallAsync(ServicePath, ServiceInterface, "ReadAlias", "s", writer => writer.WriteString("default"),
            (m, _) => m.GetBodyReader().ReadObjectPathAsString()).ConfigureAwait(false);
        return path == NoPrompt ? null : path;
    }

    private Task<(string Item, string Prompt)> CreateItemAsync(string collection, string name, byte[] secret) =>
        CallAsync(collection, CollectionInterface, "CreateItem", "a{sv}(oayays)b",
            writer =>
            {
                writer.WriteDictionary(new Dictionary<string, VariantValue>
                {
                    [ItemInterface + ".Label"] = $"PairSync {name}",
                    [ItemInterface + ".Attributes"] = new Dict<string, string>(Attributes(name)),
                });
                writer.WriteStructureStart();
                writer.WriteObjectPath(_session);
                writer.WriteArray(Array.Empty<byte>());
                writer.WriteArray(secret);
                writer.WriteString("application/octet-stream");
                writer.WriteBool(true); // replace an item with the same attributes
            },
            (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (reader.ReadObjectPathAsString(), reader.ReadObjectPathAsString());
            });

    private Task<byte[]> GetSecretAsync(string item) =>
        CallAsync(item, ItemInterface, "GetSecret", "o", writer => writer.WriteObjectPath(_session),
            (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.AlignStruct();
                reader.ReadObjectPath();   // session
                reader.ReadArrayOfByte();  // parameters, empty for "plain"
                return reader.ReadArrayOfByte();
            });

    private static Task<string> OpenSessionAsync(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: ServicePath, @interface: ServiceInterface, member: "OpenSession", signature: "sv");
        writer.WriteString("plain");
        writer.WriteVariantString("");
        return connection.CallMethodAsync(writer.CreateMessage(), (m, _) =>
        {
            var reader = m.GetBodyReader();
            reader.ReadVariantValue();
            return reader.ReadObjectPathAsString();
        });
    }

    private Task<T> CallAsync<T>(string path, string @interface, string member, string signature, Action<MessageWriter>? write,
        MessageValueReader<T> read)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: path, @interface: @interface, member: member,
            signature: signature.Length == 0 ? null : signature);
        write?.Invoke(writer);
        return _connection.CallMethodAsync(writer.CreateMessage(), read);
    }

    private static void WriteStringDictionary(MessageWriter writer, Dictionary<string, string> values)
    {
        var start = writer.WriteDictionaryStart();
        foreach (var (key, value) in values)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(key);
            writer.WriteString(value);
        }
        writer.WriteDictionaryEnd(start);
    }

    private static List<string> ToStrings(ObjectPath[] paths) => [.. paths.Select(p => p.ToString())];
}
