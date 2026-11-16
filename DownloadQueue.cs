namespace GitSync;

/// <summary>
/// Runs jobs one after another, in the order they were added, while more can
/// be added at any time: in Update All the user picks the next repository's
/// files while the previous ones download.
///
/// One at a time rather than side by side: the files usually go to the same
/// disk, often a hard drive, where several big writes at once each crawl, and
/// one finished repository is worth more than five half-downloaded ones.
/// </summary>
/// <param name="run">
/// Does one job. It handles its own failures: an exception it lets out (other
/// than the cancellation) ends the queue and surfaces like any unhandled one.
/// </param>
internal sealed class DownloadQueue<T>(Func<T, CancellationToken, Task> run)
{
    // The app only touches the queue from its window's thread; the lock is for
    // the tests, whose continuations run on the thread pool.
    private readonly Lock _gate = new();
    private readonly Queue<T> _waiting = new();
    private bool _running;

    /// <summary>Raised once the last job has finished and nothing is waiting; not after a cancellation.</summary>
    internal event EventHandler? Drained;

    /// <summary>Jobs added and not finished yet: the one running and those waiting.</summary>
    internal int Remaining
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count + (_running ? 1 : 0);
            }
        }
    }

    /// <summary>Jobs waiting for the one running to finish.</summary>
    internal int Waiting
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count;
            }
        }
    }

    /// <summary>Adds a job; it starts at once when nothing else is running.</summary>
    internal void Add(T job, CancellationToken cancel)
    {
        bool start;

        lock (_gate)
        {
            _waiting.Enqueue(job);
            start = !_running;
            _running = true;
        }

        if (start)
        {
            RunAll(cancel);
        }
    }

    /// <summary>
    /// async void on purpose: nothing awaits the queue, and a Task nobody
    /// awaits would swallow an unexpected exception and leave the queue
    /// marked as running forever. This way it reaches the window's thread,
    /// as the old one-by-one Update did.
    /// </summary>
    private async void RunAll(CancellationToken cancel)
    {
        while (true)
        {
            T job;

            lock (_gate)
            {
                if (_waiting.Count == 0 || cancel.IsCancellationRequested)
                {
                    // Cancelled, the waiting ones will never run: left in,
                    // they would count as unfinished forever.
                    _waiting.Clear();
                    _running = false;
                    break;
                }

                job = _waiting.Dequeue();
            }

            try
            {
                await run(job, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // Stopped: the loop ends at its next turn.
            }
        }

        if (!cancel.IsCancellationRequested)
        {
            Drained?.Invoke(this, EventArgs.Empty);
        }
    }
}
