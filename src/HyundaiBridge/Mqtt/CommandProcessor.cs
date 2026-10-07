using System.Security.Cryptography;
using System.Text;
using HyundaiBridge.Domain;
using HyundaiBridge.Hosting;
using Microsoft.Extensions.Logging;

namespace HyundaiBridge.Mqtt;

internal sealed class CommandProcessor(CommandJournal journal, TimeProvider time, SemaphoreSlim backendGate,
    ILogger<CommandProcessor> logger, VehicleCommandHandler? execute = null)
{
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    internal async Task ProcessAsync(VehicleCommand command, VehicleMetadata? vehicle,
        Func<CommandResult, Task> publish, Func<VehicleState, Task> observedState, CancellationToken cancellationToken,
        TimeSpan? remaining = null)
    {
        // Called by the transport's one reader; journal and backend operations
        // cannot race or interleave controls for a vehicle.
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Contract.Serialize(command))));
        if (journal.Data.Commands.TryGetValue(command.CommandId, out var previous))
        {
            await publish(previous.VehicleId == command.VehicleId && previous.Fingerprint == fingerprint
                ? previous.Result : new(command.CommandId, command.Command, "failed", "Command id was already used for another request"));
            return;
        }
        CommandResult result;
        try
        {
            if (remaining <= TimeSpan.Zero) throw new FormatException("Command expired before execution");
            if (vehicle is null) throw new FormatException("Unknown vehicle");
            Contract.Validate(command, vehicle);
            if (execute is null) throw new FormatException("Vehicle does not support this command");
            if (command.Command == "refresh" &&
                journal.Data.RefreshNotBefore.GetValueOrDefault(command.VehicleId) > time.GetUtcNow())
                throw new FormatException("Vehicle refresh cooldown is active");
        }
        catch (FormatException error)
        {
            result = new(command.CommandId, command.Command, "failed", error.Message);
            await publish(result);
            return;
        }

        var marker = new StoredCommand(command.VehicleId, fingerprint,
            new(command.CommandId, command.Command, "failed", "Command is in progress; outcome is unknown"), true);
        journal.Data.Commands[command.CommandId] = marker;
        if (command.Command == "refresh")
            journal.Data.RefreshNotBefore[command.VehicleId] = time.GetUtcNow() + PollingSchedule.RefreshCooldown;
        // A failed write prevents execution. Keep the in-memory marker as well.
        await journal.SaveAsync(cancellationToken);
        var entered = false;
        var reporting = true;
        Task<VehicleState?>? execution = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining ?? Timeout);
        try
        {
            await backendGate.WaitAsync(deadline.Token);
            entered = true;
            logger.LogInformation("Command submitted: {Command} {CommandId}", command.Command, command.CommandId);
            execution = execute!(command, async () =>
            {
                if (!Volatile.Read(ref reporting)) return;
                var accepted = new CommandResult(command.CommandId, command.Command, "accepted");
                journal.Data.Commands[command.CommandId] = marker with { Result = accepted };
                await journal.SaveAsync(deadline.Token);
                await publish(accepted);
            }, deadline.Token);
            var state = await execution.WaitAsync(deadline.Token);
            // Preserve observation ordering with polling: a later backend read
            // must not publish before this operation's observed state.
            if (state is not null) await observedState(state);
            result = new(command.CommandId, command.Command, "completed");
            logger.LogInformation("Command completed: {Command} {CommandId}", command.Command, command.CommandId);
        }
        catch (Exception error)
        {
            // Do not include adapter exception text: it may contain vendor
            // payloads, PINs, URLs or credentials. No control is retried.
            result = new(command.CommandId, command.Command, "failed", error is OperationCanceledException
                ? "Command stopped or timed out; outcome is unknown" : "Vehicle command failed; outcome may be unknown");
            logger.LogWarning("Command failed: {Command} {CommandId}; {FailureType}",
                command.Command, command.CommandId, error.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref reporting, false);
            if (entered)
            {
                if (execution is { IsCompleted: false })
                    _ = execution.ContinueWith(task => { _ = task.Exception; backendGate.Release(); },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                else backendGate.Release();
            }
        }
        journal.Data.Commands[command.CommandId] = marker with { Result = result, InProgress = false };
        await journal.SaveAsync(CancellationToken.None);
        await publish(result);
    }
}
