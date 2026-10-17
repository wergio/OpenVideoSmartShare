using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using OpenVideoSmartShare.Infrastructure;

namespace OpenVideoSmartShare.Upnp;

/// <summary>Minimal UPnP GENA eventing: SUBSCRIBE/UNSUBSCRIBE and NOTIFY of evented variables.</summary>
internal sealed class EventSubscriptions : IDisposable
{
    private const int DefaultTimeoutSeconds = 1800;

    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Handles SUBSCRIBE; returns (SID, timeout) or null when the request is invalid.</summary>
    public (string Sid, int TimeoutSeconds)? Subscribe(string service, string? sid, string? callback, string? timeout,
        Func<string> initialProperties)
    {
        var seconds = ParseTimeout(timeout);
        if (!string.IsNullOrEmpty(sid))
        {
            if (!_subscriptions.TryGetValue(sid, out var existing) || existing.Service != service)
            {
                return null;
            }

            existing.Expires = DateTime.UtcNow.AddSeconds(seconds);
            return (sid, seconds);
        }

        var callbacks = ParseCallbacks(callback);
        if (callbacks.Count == 0)
        {
            return null;
        }

        var subscription = new Subscription(service, "uuid:" + Guid.NewGuid(), callbacks)
        {
            Expires = DateTime.UtcNow.AddSeconds(seconds),
        };
        _subscriptions[subscription.Sid] = subscription;

        // The initial event must follow the SUBSCRIBE response.
        _ = Task.Run(async () =>
        {
            await Task.Delay(200).ConfigureAwait(false);
            await SendAsync(subscription, initialProperties()).ConfigureAwait(false);
        });
        return (subscription.Sid, seconds);
    }

    public bool Unsubscribe(string? sid) => sid != null && _subscriptions.TryRemove(sid, out _);

    /// <summary>Sends a property change to every live subscriber of <paramref name="service"/>.</summary>
    public void Notify(string service, string propertySetXml)
    {
        foreach (var subscription in _subscriptions.Values)
        {
            if (subscription.Expires < DateTime.UtcNow)
            {
                _subscriptions.TryRemove(subscription.Sid, out _);
                continue;
            }

            if (subscription.Service == service)
            {
                _ = SendAsync(subscription, propertySetXml);
            }
        }
    }

    public static string PropertySet(params (string Name, string Value)[] properties)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\">");
        foreach (var (name, value) in properties)
        {
            sb.Append("<e:property><").Append(name).Append('>').Append(Didl.Escape(value))
              .Append("</").Append(name).Append("></e:property>");
        }

        return sb.Append("</e:propertyset>").ToString();
    }

    public void Dispose() => _http.Dispose();

    private async Task SendAsync(Subscription subscription, string body)
    {
        uint seq;
        lock (subscription)
        {
            seq = subscription.Sequence;
            subscription.Sequence = subscription.Sequence == uint.MaxValue ? 1 : subscription.Sequence + 1;
        }

        foreach (var callback in subscription.Callbacks)
        {
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod("NOTIFY"), callback)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/xml"),
                };
                request.Headers.TryAddWithoutValidation("NT", "upnp:event");
                request.Headers.TryAddWithoutValidation("NTS", "upnp:propchange");
                request.Headers.TryAddWithoutValidation("SID", subscription.Sid);
                request.Headers.TryAddWithoutValidation("SEQ", seq.ToString(CultureInfo.InvariantCulture));
                using var response = await _http.SendAsync(request).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                Log.Warn($"Event delivery failed to {callback}", ex);
            }
        }
    }

    private static int ParseTimeout(string? header)
    {
        if (header != null && header.StartsWith("Second-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(header.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return Math.Min(seconds, 86400);
        }

        return DefaultTimeoutSeconds;
    }

    private static List<Uri> ParseCallbacks(string? header)
    {
        var result = new List<Uri>();
        if (string.IsNullOrEmpty(header))
        {
            return result;
        }

        foreach (var part in header.Split('<', '>'))
        {
            if (Uri.TryCreate(part.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
            {
                result.Add(uri);
            }
        }

        return result;
    }

    private sealed class Subscription(string service, string sid, List<Uri> callbacks)
    {
        public string Service { get; } = service;

        public string Sid { get; } = sid;

        public List<Uri> Callbacks { get; } = callbacks;

        public DateTime Expires { get; set; }

        public uint Sequence { get; set; }
    }
}
