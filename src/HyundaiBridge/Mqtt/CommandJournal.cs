using System.Text.Json;
using HyundaiBridge.Domain;

namespace HyundaiBridge.Mqtt;

internal sealed record StoredCommand(string VehicleId, string Fingerprint, CommandResult Result, bool InProgress);
internal sealed class JournalData
{
    public required int Version { get; set; }
    public required Dictionary<Guid, StoredCommand> Commands { get; set; }
    public required Dictionary<string, DateTimeOffset> RefreshNotBefore { get; set; }
}

// One bridge process owns this directory. Save the operation marker before a
// control can reach the adapter, so restart/redelivery cannot execute it again.
internal sealed class CommandJournal(string directory)
{
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private string Path => System.IO.Path.Combine(directory, "commands.json");
    internal JournalData Data { get; private set; } = new() { Version = 1, Commands = [], RefreshNotBefore = [] };

    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) throw new IOException("Private bridge storage requires macOS or Linux");
        Directory.CreateDirectory(directory, OwnerDirectory);
        File.SetUnixFileMode(directory, OwnerDirectory);
        if (!File.Exists(Path)) return;
        File.SetUnixFileMode(Path, OwnerFile);
        await using var stream = File.OpenRead(Path);
        try
        {
            Data = await JsonSerializer.DeserializeAsync<JournalData>(stream, cancellationToken: cancellationToken)
                ?? throw new JsonException();
            if (Data.Version != 1 || Data.Commands is null || Data.RefreshNotBefore is null ||
                Data.Commands.Any(x => x.Key == Guid.Empty || x.Value is null || x.Value.Result is null ||
                    x.Value.Result.CommandId != x.Key || !Contract.ValidId(x.Value.VehicleId) ||
                    x.Value.Fingerprint is not { Length: 64 } || !Contract.Commands.Contains(x.Value.Result.Command) ||
                    x.Value.Result.Status is not ("accepted" or "completed" or "failed")))
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new IOException("Invalid command journal; refusing controls to avoid duplicate execution");
        }
        foreach (var (id, command) in Data.Commands.ToArray())
            if (command.InProgress)
                Data.Commands[id] = command with
                {
                    InProgress = false,
                    Result = command.Result with
                    { Status = "failed", Message = "Bridge restarted during command; outcome is unknown" }
                };
        await SaveAsync(cancellationToken);
    }

    internal async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) throw new IOException("Private bridge storage requires macOS or Linux");
        // Bound disk/memory usage. Recent 2048 terminal IDs remain deduplicated;
        // in-progress markers are never evicted. Clients must never reuse IDs.
        foreach (var id in Data.Commands.Where(x => !x.Value.InProgress)
            .Take(Math.Max(0, Data.Commands.Count - 2048)).Select(x => x.Key).ToArray())
            Data.Commands.Remove(id);
        var temporary = System.IO.Path.Combine(directory, $".commands-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = OwnerFile
            }))
            {
                await JsonSerializer.SerializeAsync(stream, Data, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
