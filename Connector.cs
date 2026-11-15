using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace GitSync;

/// <summary>
/// Opens the connections of every request, trying a host's IPv6 and IPv4
/// addresses side by side ("Happy Eyeballs", RFC 8305) instead of one after the
/// other.
///
/// The trap: some networks give the PC an IPv6 address but let no IPv6 traffic
/// through. Windows then lists a host's IPv6 address first (gitlab.com and
/// codeberg.org have one; github.com has none), and .NET's own connect tries
/// the addresses in turn. Windows waits 21 s on an address that never answers,
/// past the 20 s ConnectTimeout, so the IPv4 address was never reached and
/// every request ended in "did not answer in time" — while the site opened
/// fine in a browser, which races both.
/// </summary>
internal static class Connector
{
    /// <summary>RFC 8305's "Connection Attempt Delay": how long one address gets before the next joins in.</summary>
    internal static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The address family that last connected to each host, tried first from
    /// then on, so only the first connection to a host can lose the 250 ms.
    /// Kept until the app closes, not saved: the next launch may be on another
    /// network. Still a preference, not a rule — if that family stops
    /// answering, the other one joins in after the delay as before.
    /// </summary>
    private static readonly ConcurrentDictionary<string, AddressFamily> Worked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The family that last connected to <paramref name="host"/>, if any yet.</summary>
    internal static AddressFamily? Remembered(string host) =>
        Worked.TryGetValue(host, out AddressFamily family) ? family : null;

    internal static async ValueTask<Stream> ConnectAsync(DnsEndPoint target, CancellationToken cancel)
    {
        try
        {
            IPAddress[] found = await Dns.GetHostAddressesAsync(target.Host, cancel);
            Socket socket = await ConnectFirstAsync(Order(found, Remembered(target.Host)), target.Port, AttemptDelay, cancel);
            Worked[target.Host] = socket.AddressFamily;

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The handler reports ConnectTimeout only for a cancellation that
            // carries its own token; any other comes out as a connection error.
            cancel.ThrowIfCancellationRequested();
            throw;
        }
    }

    /// <summary>
    /// The order to try the addresses in: the family that worked before (or, the
    /// first time, the one Windows lists first), then alternating families, so a
    /// broken family costs one delay and not one per address it has.
    /// </summary>
    internal static List<IPAddress> Order(IReadOnlyList<IPAddress> addresses, AddressFamily? remembered)
    {
        if (addresses.Count == 0)
        {
            return [];
        }

        AddressFamily first = remembered is { } r && addresses.Any(a => a.AddressFamily == r)
            ? r
            : addresses[0].AddressFamily;

        List<IPAddress> mine = [.. addresses.Where(a => a.AddressFamily == first)];
        List<IPAddress> other = [.. addresses.Where(a => a.AddressFamily != first)];
        var order = new List<IPAddress>(addresses.Count);

        for (int i = 0; i < Math.Max(mine.Count, other.Count); i++)
        {
            if (i < mine.Count)
            {
                order.Add(mine[i]);
            }

            if (i < other.Count)
            {
                order.Add(other[i]);
            }
        }

        return order;
    }

    /// <summary>
    /// Starts on the first address; each next one starts when the delay passes
    /// without a connection, or at once when an attempt fails. The first to
    /// connect wins and the rest are dropped. When all fail, the first failure
    /// is thrown; <paramref name="cancel"/> (ConnectTimeout included) cancels
    /// with that same token, which is how the handler tells a timeout apart.
    /// </summary>
    internal static async Task<Socket> ConnectFirstAsync(
        IReadOnlyList<IPAddress> addresses, int port, TimeSpan delay, CancellationToken cancel)
    {
        if (addresses.Count == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        using var stopOthers = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var running = new List<Task<Socket>>();
        Exception? firstError = null;
        int next = 0;

        try
        {
            while (true)
            {
                if (next < addresses.Count)
                {
                    running.Add(AttemptAsync(addresses[next++], port, stopOthers.Token));
                }

                if (running.Count == 0)
                {
                    ExceptionDispatchInfo.Throw(firstError!);
                }

                Task wait = next < addresses.Count
                    ? Task.Delay(delay, cancel)
                    : Task.Delay(Timeout.InfiniteTimeSpan, cancel);

                Task[] watched = [.. running, wait];
                Task done = await Task.WhenAny(watched);

                // Checked first: a cancelled attempt would otherwise pass for a failed one.
                cancel.ThrowIfCancellationRequested();

                if (done == wait)
                {
                    continue;
                }

                var attempt = (Task<Socket>)done;
                running.Remove(attempt);

                if (attempt.IsCompletedSuccessfully)
                {
                    return attempt.Result;
                }

                firstError ??= attempt.Exception?.InnerException ?? new SocketException((int)SocketError.TimedOut);
            }
        }
        finally
        {
            stopOthers.Cancel();

            // An attempt can still connect after another won; its socket is closed.
            foreach (Task<Socket> loser in running)
            {
                _ = loser.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        t.Result.Dispose();
                    }
                    else
                    {
                        _ = t.Exception;
                    }
                }, TaskScheduler.Default);
            }
        }
    }

    private static async Task<Socket> AttemptAsync(IPAddress address, int port, CancellationToken cancel)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(address, port, cancel);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
