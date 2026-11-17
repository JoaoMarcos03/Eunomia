namespace Eunomia.Engine.Transfer;

/// <summary>
/// An error of a local file of a transfer (a full disk, a file another program holds open, a lost network share): the
/// transfer failed, the server connection did not. Deliberately not an <see cref="IOException"/>: transfers read and
/// write files inside a job connection, and the driver translation reads any IOException there as a lost connection
/// (the session reconnected and the message blamed the server).
/// </summary>
public sealed class LocalFileException(IOException inner) : Exception(inner.Message, inner)
{
    /// <summary>Runs a file operation (open, move, create a directory) and turns its <see cref="IOException"/> into a <see cref="LocalFileException"/>.</summary>
    public static void Guard(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            operation();
        }
        catch (IOException e)
        {
            throw new LocalFileException(e);
        }
    }

    /// <inheritdoc cref="Guard(Action)"/>
    public static T Guard<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return operation();
        }
        catch (IOException e)
        {
            throw new LocalFileException(e);
        }
    }
}
