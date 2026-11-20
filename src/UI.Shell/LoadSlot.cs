namespace Eunomia.UI.Shell;

/// <summary>
/// One load at a time for a target (an editor, a list): <see cref="Begin"/> cancels the previous load and returns its
/// ticket, whose <see cref="Ticket.IsCurrent"/> says whether a late result may still be shown.
/// </summary>
internal sealed class LoadSlot
{
    private CancellationTokenSource? _current;

    public Ticket Begin()
    {
        _current?.Cancel();
        var cts = new CancellationTokenSource();
        _current = cts;
        return new Ticket(this, cts);
    }

    public void Cancel() => _current?.Cancel();

    internal readonly record struct Ticket(LoadSlot Slot, CancellationTokenSource Source)
    {
        public CancellationToken Token => Source.Token;

        public bool IsCurrent => ReferenceEquals(Slot._current, Source) && !Source.IsCancellationRequested;
    }
}
