using System.Net;

namespace OpenVideoSmartShare.Upnp;

/// <summary>
/// Knows which clients are streaming a video from the server. A client counts as streaming
/// once a media response has been open for <see cref="MinDuration"/> (TVs also open files
/// briefly to probe them) and until <see cref="Linger"/> after its last such response ended
/// (a seek closes the connection and opens a new one).
/// </summary>
internal sealed class StreamTracker
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(4);

    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly List<ActiveStream> _active = [];
    private readonly Dictionary<IPAddress, DateTime> _lastEnded = [];

    public StreamTracker(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Registers a media response to <paramref name="client"/>; dispose it when the response ends.</summary>
    public IDisposable Begin(IPAddress client)
    {
        var stream = new ActiveStream(this, client, _clock());
        lock (_gate)
        {
            _active.Add(stream);
        }

        return stream;
    }

    /// <summary>Clients streaming right now.</summary>
    public IReadOnlyCollection<IPAddress> ActiveClients()
    {
        var now = _clock();
        lock (_gate)
        {
            var clients = new HashSet<IPAddress>();
            foreach (var stream in _active)
            {
                if (!stream.Ignored && now - stream.Started >= MinDuration)
                {
                    clients.Add(stream.Client);
                }
            }

            foreach (var (client, ended) in _lastEnded)
            {
                if (now - ended < Linger)
                {
                    clients.Add(client);
                }
            }

            return clients;
        }
    }

    /// <summary>
    /// Forgets the current streams of <paramref name="client"/> (playback was stopped): only
    /// responses that start afterwards count again.
    /// </summary>
    public void Reset(IPAddress client)
    {
        lock (_gate)
        {
            _lastEnded.Remove(client);
            foreach (var stream in _active.Where(s => s.Client.Equals(client)))
            {
                stream.Ignored = true;
            }
        }
    }

    private void End(ActiveStream stream)
    {
        var now = _clock();
        lock (_gate)
        {
            if (_active.Remove(stream) && !stream.Ignored && now - stream.Started >= MinDuration)
            {
                _lastEnded[stream.Client] = now;
            }
        }
    }

    private sealed class ActiveStream(StreamTracker owner, IPAddress client, DateTime started) : IDisposable
    {
        public IPAddress Client { get; } = client;

        public DateTime Started { get; } = started;

        public bool Ignored { get; set; }

        public void Dispose() => owner.End(this);
    }
}
