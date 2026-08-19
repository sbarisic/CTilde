namespace CTilde.Cli;

internal sealed class BuildLock : IAsyncDisposable
{
    private readonly FileStream stream;
    private readonly string path;

    private BuildLock(FileStream stream, string path)
    {
        this.stream = stream;
        this.path = path;
    }

    public static BuildLock Acquire(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".ctilde-build.lock");
        try
        {
            return new BuildLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BuildLockException($"Another C~ native build is already using '{directory}'.", exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        stream.Dispose();
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed class BuildLockException(string message, Exception innerException) : Exception(message, innerException);
