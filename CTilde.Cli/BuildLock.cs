using System.Text.Json;

namespace CTilde.Cli;

internal sealed record BuildLockOwner(int ProcessId, string Operation, string? Manifest, DateTimeOffset StartedAtUtc);

internal sealed class BuildLock : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private readonly FileStream stream;

    private BuildLock(FileStream stream) => this.stream = stream;

    public static Task<BuildLock> AcquireAsync(string directory, string operation, string? manifest,
        CancellationToken cancellationToken) =>
        AcquireAsync(directory, operation, manifest, DefaultTimeout, TimeProvider.System, cancellationToken);

    internal static async Task<BuildLock> AcquireAsync(string directory, string operation, string? manifest,
        TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".ctilde-build.lock");
        var started = timeProvider.GetUtcNow();
        var announced = false;
        BuildLockOwner? lastOwner = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? acquired = null;
            try
            {
                acquired = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                var owner = new BuildLockOwner(Environment.ProcessId, operation, manifest is null ? null : Path.GetFullPath(manifest), timeProvider.GetUtcNow());
                acquired.SetLength(0);
                await JsonSerializer.SerializeAsync(acquired, owner, cancellationToken: cancellationToken);
                await acquired.FlushAsync(cancellationToken);
                acquired.Position = 0;
                var result = new BuildLock(acquired);
                acquired = null;
                return result;
            }
            catch (IOException exception)
            {
                acquired?.Dispose();
                lastOwner = ReadOwner(path) ?? lastOwner;
                if (!announced)
                {
                    BuildReporter.Current?.WaitingForLock(directory, lastOwner);
                    announced = true;
                }
                if (timeProvider.GetUtcNow() - started >= timeout)
                    throw new BuildLockException(directory, lastOwner, exception);
                await Task.Delay(PollInterval, timeProvider, cancellationToken);
            }
            catch (UnauthorizedAccessException exception)
            {
                acquired?.Dispose();
                throw new BuildLockException(directory, ReadOwner(path), exception);
            }
            finally
            {
                acquired?.Dispose();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        stream.Dispose();
        return ValueTask.CompletedTask;
    }

    private static BuildLockOwner? ReadOwner(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<BuildLockOwner>(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

internal sealed class BuildLockException : Exception
{
    public BuildLockException(string directory, BuildLockOwner? owner, Exception innerException)
        : base(ComposeMessage(directory, owner), innerException) => Owner = owner;

    public BuildLockOwner? Owner { get; }

    private static string ComposeMessage(string directory, BuildLockOwner? owner)
    {
        var details = owner is null
            ? string.Empty
            : $" Owner PID {owner.ProcessId} started {owner.Operation} at {owner.StartedAtUtc:O}" +
              (owner.Manifest is null ? "." : $" for '{owner.Manifest}'.");
        return $"Timed out after 30 seconds waiting for another C~ project build or binding refresh using '{directory}'.{details}";
    }
}
