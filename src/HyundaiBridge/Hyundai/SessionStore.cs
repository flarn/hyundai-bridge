using System.Text.Json;

namespace HyundaiBridge.Hyundai;

internal sealed class SessionStore(string directory)
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private string Path => System.IO.Path.Combine(directory, "session.json");

    private void PrepareDirectory()
    {
        if (OperatingSystem.IsWindows())
            throw new HyundaiException("Session storage currently requires macOS or Linux owner-only permissions.");
        Directory.CreateDirectory(directory, DirectoryMode);
        File.SetUnixFileMode(directory, DirectoryMode);
    }

    internal async Task<HyundaiSession?> LoadAsync(string accountHash, CancellationToken cancellationToken)
    {
        PrepareDirectory();
        if (!File.Exists(Path)) return null;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, FileMode);
        await using var stream = File.OpenRead(Path);
        HyundaiSession? session;
        try
        {
            session = await JsonSerializer.DeserializeAsync<HyundaiSession>(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new HyundaiException("Saved session is invalid. Remove the private session file before retrying.");
        }
        if (session is null || string.IsNullOrWhiteSpace(session.DeviceId) ||
            string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.RefreshToken) ||
            string.IsNullOrWhiteSpace(session.NonCcsToken) || string.IsNullOrWhiteSpace(session.ExchangeableAccessToken))
            throw new HyundaiException("Saved session is incomplete. Remove the private session file before retrying.");
        if (session.AccountHash != accountHash) return null;
        return session;
    }

    internal async Task SaveAsync(HyundaiSession session, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            throw new HyundaiException("Session storage currently requires macOS or Linux owner-only permissions.");
        PrepareDirectory();
        var temporary = System.IO.Path.Combine(directory, $".session-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous, UnixCreateMode = FileMode
            }))
            {
                await JsonSerializer.SerializeAsync(stream, session, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
