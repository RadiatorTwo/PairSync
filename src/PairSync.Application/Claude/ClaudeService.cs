using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PairSync.Application.Connections;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage;
using PairSync.Storage.Settings;
using PairSync.Transport;

namespace PairSync.Application.Claude;

/// <summary>The target of a Claude Code review: its state and what it allows this device.</summary>
public sealed record ClaudeTarget(PairedDevice Device, ClaudeSnapshot Snapshot, bool AllowPrograms, IReadOnlyList<string> PathVariables);

/// <summary>The target cannot be asked: not reachable, too old, or it does not allow this device.</summary>
public sealed class ClaudeUnavailableException(string message, bool notAllowed = false) : Exception(message)
{
    public bool NotAllowed { get; } = notAllowed;
}

/// <summary>CLI steps that arrived while Claude Code was not installed here; applied from the Claude Code page later.</summary>
public sealed record PendingClaudePlan(string DeviceName, DateTime ReceivedAtUtc, IReadOnlyList<ClaudeStep> Steps);

/// <summary>What the target reported after "Apply".</summary>
public sealed record ClaudeApplyOutcome(string? Error, int FilesWritten, int SettingsWritten, IReadOnlyList<string> Notes, string? BackupFolder);

/// <summary>
/// Claude Code provider (plan §9, phase 4). As the source it reads this device, asks the target for its state and
/// sends what the user chose; as the target it answers with its state and applies what the source sends, within
/// the permissions it gave that device ("Apply Claude config", "Install programs").
/// </summary>
public sealed class ClaudeService(PeerLinks links, DataDirectory data, SettingsStore settings, ClaudeOptions options, ILogger<ClaudeService> logger)
{
    private const int FileDataBytes = 512 * 1024;
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _applying = new(1, 1);

    private string PendingPath => Path.Combine(data.Root, "claude-pending.json");

    /// <summary>The pending plan appeared, was applied or discarded.</summary>
    public event Action? Changed;

    public static bool IsClaudeMessage(IControlMessage first) => first is ClaudeStateRequest or ClaudeApply;

    public ClaudeEnvironment Environment => ClaudeLocator.Locate(options);

    /// <summary>This device's Claude Code configuration, including the CLI version if it is installed.</summary>
    public Task<ClaudeSnapshot> ReadLocalAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var program = ClaudeLocator.FindProgram(options);
        var version = program is null ? null : await ClaudeLocator.GetVersionAsync(program, cancellationToken).ConfigureAwait(false);
        return ClaudeInventory.Read(ClaudeLocator.Locate(options), version);
    }, cancellationToken);

    // ---- source ----

    /// <exception cref="ClaudeUnavailableException">Not reachable, too old or not allowed.</exception>
    public async Task<ClaudeTarget> FetchTargetAsync(PairedDevice device, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await ConnectAsync(device, cancellationToken).ConfigureAwait(false);
            await connection.Channels.Control.SendAsync(new ClaudeStateRequest(), cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StateTimeout);
            await foreach (var message in connection.Channels.Control.ReadAllAsync(timeout.Token).ConfigureAwait(false))
            {
                if (message is not ClaudeState state)
                    continue;
                if (!state.Allowed)
                    throw new ClaudeUnavailableException($"{device.Name} does not allow this device to apply Claude Code configuration. Allow it there under Devices → Permissions.", notAllowed: true);
                return new ClaudeTarget(device, ClaudeWire.FromState(state), state.AllowPrograms, state.PathVariables ?? []);
            }
            throw new ClaudeUnavailableException($"{device.Name} did not answer.");
        }
        catch (Exception e) when (e is TransportException or ProtocolException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ClaudeUnavailableException(e is OperationCanceledException ? $"{device.Name} did not answer in time." : e.Message);
        }
    }

    /// <summary>Sends the chosen files, settings and steps; <paramref name="progress"/> gets each step's result.</summary>
    /// <exception cref="ClaudeUnavailableException">The connection failed before the target finished.</exception>
    public async Task<ClaudeApplyOutcome> ApplyAsync(PairedDevice device, ClaudeApplyRequest request, Action<ClaudeStepResult> progress, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await ConnectAsync(device, cancellationToken).ConfigureAwait(false);
            var control = connection.Channels.Control;
            await control.SendAsync(ClaudeWire.ToWire(request), cancellationToken).ConfigureAwait(false);
            var configDir = Environment.ConfigDir;
            var buffer = new byte[FileDataBytes];
            foreach (var file in request.Files)
            {
                await using var stream = File.OpenRead(Path.Combine(configDir, file.Path));
                long offset = 0;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    // A file that grew since the plan still ends at the announced size; the target checks the hash.
                    read = (int)Math.Min(read, file.Size - offset);
                    var last = read == 0 || offset + read >= file.Size;
                    await control.SendAsync(new ClaudeFileData { Path = file.Path, Offset = offset, Data = buffer[..read], Last = last }, cancellationToken)
                        .ConfigureAwait(false);
                    offset += read;
                    if (last)
                        break;
                }
            }

            // Steps can take minutes (plugin downloads); the limit is per message.
            {
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                quiet.CancelAfter(options.StepTimeout * 2 + TimeSpan.FromMinutes(1));
                var enumerator = control.ReadAllAsync(quiet.Token).GetAsyncEnumerator(quiet.Token);
                try
                {
                    while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        switch (enumerator.Current)
                        {
                            case ClaudeStepResult result:
                                progress(result);
                                quiet.CancelAfter(options.StepTimeout * 2 + TimeSpan.FromMinutes(1));
                                break;
                            case ClaudeApplyDone done:
                                return new ClaudeApplyOutcome(done.Error, done.FilesWritten, done.SettingsWritten, done.Notes ?? [], done.BackupFolder);
                        }
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                throw new ClaudeUnavailableException($"The connection to {device.Name} ended before it finished.");
            }
        }
        catch (Exception e) when (e is TransportException or ProtocolException or IOException
                                      or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ClaudeUnavailableException(e is OperationCanceledException ? $"{device.Name} stopped answering." : e.Message);
        }
    }

    private async Task<PeerConnection> ConnectAsync(PairedDevice device, CancellationToken cancellationToken)
    {
        if (!links.IsReachable(device.Id))
            throw new ClaudeUnavailableException($"{device.Name} is not reachable.");
        var connection = await links.ConnectAsync(device, cancellationToken).ConfigureAwait(false);
        if (connection.Handshake.RemoteMinor < 6)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new ClaudeUnavailableException($"{device.Name} needs a newer PairSync for Claude Code.");
        }
        return connection;
    }

    // ---- target ----

    /// <summary>Handles a session another device opened with a Claude message; owns the connection.</summary>
    public async Task HandleIncomingAsync(PeerConnection connection, IControlMessage first)
    {
        await using var owned = connection;
        var device = connection.Device!;
        var control = connection.Channels.Control;
        using var session = new CancellationTokenSource(TimeSpan.FromHours(1));
        try
        {
            switch (first)
            {
                case ClaudeStateRequest:
                    await control.SendAsync(await StateForAsync(device, session.Token).ConfigureAwait(false), session.Token).ConfigureAwait(false);
                    await LingerAsync(connection, session.Token).ConfigureAwait(false);
                    break;
                case ClaudeApply apply:
                    var done = await ApplyIncomingAsync(connection, device, apply, session.Token).ConfigureAwait(false);
                    await control.SendAsync(done, session.Token).ConfigureAwait(false);
                    await LingerAsync(connection, session.Token).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception e) when (e is TransportException or ProtocolException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            logger.LogInformation(e, "Claude Code session with {Name} ended", device.Name);
        }
    }

    /// <summary>Gives the other side time to read the last message before the session closes.</summary>
    private static async Task LingerAsync(PeerConnection connection, CancellationToken cancellationToken)
    {
        using var linger = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linger.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var _ in connection.Channels.Control.ReadAllAsync(linger.Token).ConfigureAwait(false))
            {
            }
        }
        catch (Exception e) when (e is OperationCanceledException or TransportException)
        {
        }
    }

    private async Task<ClaudeState> StateForAsync(PairedDevice device, CancellationToken cancellationToken)
    {
        if (!device.CanApplyClaudeConfig)
        {
            logger.LogInformation("{Name} asked for the Claude Code configuration; not allowed", device.Name);
            return new ClaudeState { Allowed = false };
        }
        var snapshot = await ReadLocalAsync(cancellationToken).ConfigureAwait(false);
        var names = PathMapper.TargetVariables(snapshot.Environment, settings.Current.PathVariables).Keys.Order(StringComparer.Ordinal);
        return ClaudeWire.ToState(snapshot, device.CanInstallPrograms, names);
    }

    private async Task<ClaudeApplyDone> ApplyIncomingAsync(PeerConnection connection, PairedDevice device, ClaudeApply apply, CancellationToken cancellationToken)
    {
        if (!device.CanApplyClaudeConfig)
            return new ClaudeApplyDone { Error = "This device does not allow the other device to apply Claude Code configuration." };
        if (!await _applying.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new ClaudeApplyDone { Error = "Another apply is running on this device." };
        try
        {
            IReadOnlyList<ClaudeFile> files;
            IReadOnlyList<SettingChange> changes;
            IReadOnlyList<ClaudeStep> steps;
            try
            {
                files = ClaudeApplier.CheckFiles(apply.Files);
                changes = ClaudeApplier.CheckSettings(apply.Settings);
                steps = ClaudeApplier.CheckSteps(apply.Steps);
            }
            catch (ClaudeApplyRejectedException e)
            {
                logger.LogWarning("Claude Code apply from {Name} rejected: {Reason}", device.Name, e.Message);
                return new ClaudeApplyDone { Error = e.Message };
            }

            var environment = Environment;
            var applier = NewApplier(environment, device.CanInstallPrograms);
            var incoming = connection.Channels.Control.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using (incoming.ConfigureAwait(false))
            {
                IReadOnlyList<string> temps;
                try
                {
                    temps = await applier.ReceiveAsync(files, incoming, cancellationToken).ConfigureAwait(false);
                }
                catch (ClaudeApplyRejectedException e)
                {
                    return new ClaudeApplyDone { Error = e.Message };
                }
                logger.LogInformation("Applying Claude Code configuration from {Name}: {Files} files, {Settings} settings, {Steps} steps",
                    device.Name, files.Count, changes.Count, steps.Count);
                int filesWritten, settingsWritten;
                try
                {
                    filesWritten = applier.WriteFiles(files, temps);
                    settingsWritten = applier.WriteSettings(changes);
                }
                catch (ClaudeApplyRejectedException e)
                {
                    return new ClaudeApplyDone { Error = e.Message };
                }

                var control = connection.Channels.Control;
                var runnable = new List<(int Index, ClaudeStep Step)>();
                for (var i = 0; i < steps.Count; i++)
                {
                    if (ClaudeApplier.RunsPrograms(steps[i]) && !device.CanInstallPrograms)
                        await control.SendAsync(new ClaudeStepResult { Index = i, Status = ClaudeStepStatus.Skipped, Output = "This device does not allow installing programs." },
                            cancellationToken).ConfigureAwait(false);
                    else
                        runnable.Add((i, steps[i]));
                }

                var program = ClaudeLocator.FindProgram(options);
                if (program is null && runnable.Count > 0)
                {
                    SavePending(new PendingClaudePlan(device.Name, DateTime.UtcNow, [.. runnable.Select(r => r.Step)]));
                    foreach (var (index, _) in runnable)
                        await control.SendAsync(new ClaudeStepResult { Index = index, Status = ClaudeStepStatus.Pending, Output = "Claude Code is not installed on this device; the step waits there." },
                            cancellationToken).ConfigureAwait(false);
                }
                else if (program is not null)
                {
                    var existing = ClaudeInventory.Read(environment, null).McpServers;
                    foreach (var (index, step) in runnable)
                    {
                        var result = await ClaudeApplier.RunAsync(applier, program, step, index, existing, options.StepTimeout, cancellationToken).ConfigureAwait(false);
                        logger.LogInformation("Claude step {Title}: exit code {ExitCode}\n{Output}", ClaudeWire.StepTitle(step), result.ExitCode, result.Output);
                        await control.SendAsync(result, cancellationToken).ConfigureAwait(false);
                    }
                }
                return new ClaudeApplyDone
                {
                    FilesWritten = filesWritten, SettingsWritten = settingsWritten, Notes = [.. applier.Notes], BackupFolder = applier.BackupFolder,
                };
            }
        }
        finally
        {
            _applying.Release();
        }
    }

    private ClaudeApplier NewApplier(ClaudeEnvironment environment, bool allowPrograms) =>
        new(environment, data.Root, PathMapper.TargetVariables(environment, settings.Current.PathVariables), allowPrograms);

    // ---- pending plan ----

    /// <summary>Steps that wait for Claude Code to be installed on this device.</summary>
    public PendingClaudePlan? Pending
    {
        get
        {
            var root = ClaudeInventory.ReadObject(PendingPath);
            if (root?["steps"] is not JsonArray array)
                return null;
            try
            {
                var steps = ClaudeApplier.CheckSteps([.. array.OfType<JsonObject>().Select(s => new ClaudeStepEntry
                {
                    Kind = (int?)s["kind"] ?? -1, Name = (string?)s["name"], Value = (string?)s["value"],
                })]);
                return new PendingClaudePlan((string?)root["device"] ?? "", (DateTime?)root["receivedAtUtc"] ?? DateTime.UtcNow, steps);
            }
            catch (Exception e) when (e is ClaudeApplyRejectedException or FormatException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    private void SavePending(PendingClaudePlan plan)
    {
        var root = new JsonObject
        {
            ["device"] = plan.DeviceName,
            ["receivedAtUtc"] = plan.ReceivedAtUtc,
            ["steps"] = new JsonArray([.. plan.Steps.Select(s => new JsonObject { ["kind"] = (int)s.Kind, ["name"] = s.Name, ["value"] = s.Value })]),
        };
        var temp = PendingPath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, PendingPath, overwrite: true);
        Changed?.Invoke();
    }

    public void DiscardPending()
    {
        File.Delete(PendingPath);
        Changed?.Invoke();
    }

    /// <summary>
    /// Runs the pending steps here. They were confirmed on the other device when it sent them; permissions were
    /// checked on arrival. Failed steps are reported and the plan is removed either way.
    /// </summary>
    /// <exception cref="ClaudeUnavailableException">Claude Code is still not installed.</exception>
    public async Task<IReadOnlyList<ClaudeStepResult>> ApplyPendingAsync(Action<ClaudeStepResult> progress, CancellationToken cancellationToken)
    {
        var plan = Pending ?? throw new ClaudeUnavailableException("There is no pending plan.");
        var program = ClaudeLocator.FindProgram(options) ?? throw new ClaudeUnavailableException("Claude Code is still not installed on this device.");
        await _applying.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var environment = Environment;
            var applier = NewApplier(environment, allowPrograms: true);
            var existing = ClaudeInventory.Read(environment, null).McpServers;
            var results = new List<ClaudeStepResult>();
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                var result = await ClaudeApplier.RunAsync(applier, program, plan.Steps[i], i, existing, options.StepTimeout, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Pending Claude step {Title}: exit code {ExitCode}\n{Output}", ClaudeWire.StepTitle(plan.Steps[i]), result.ExitCode, result.Output);
                results.Add(result);
                progress(result);
            }
            DiscardPending();
            return results;
        }
        finally
        {
            _applying.Release();
        }
    }
}
