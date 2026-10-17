using System.Net.Http.Headers;
using System.Text;
using OpenVideoSmartShare.Infrastructure;

namespace OpenVideoSmartShare.Upnp;

/// <summary>
/// AVTransport:1 control point. Playback is Stop (when something is loaded),
/// SetAVTransportURI with the DIDL-Lite metadata, then Play(Speed=1).
/// </summary>
internal sealed class AvTransportClient : IDisposable
{
    public const string ServiceType = "urn:schemas-upnp-org:service:AVTransport:1";

    private static readonly string[] BusyStates = ["PLAYING", "PAUSED_PLAYBACK", "TRANSITIONING", "RECORDING", "PAUSED_RECORDING"];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task PlayAsync(RendererInfo renderer, string uri, string metadata, CancellationToken cancellationToken = default)
    {
        var state = await GetTransportStateAsync(renderer, cancellationToken).ConfigureAwait(false);
        if (state != null && BusyStates.Contains(state, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await StopAsync(renderer, cancellationToken).ConfigureAwait(false);
            }
            catch (UpnpException ex)
            {
                Log.Warn($"Stop before play failed on {renderer.FriendlyName}: {ex.Code} {ex.Message}");
            }
        }

        await InvokeAsync(renderer, "SetAVTransportURI", cancellationToken,
            ("InstanceID", "0"), ("CurrentURI", uri), ("CurrentURIMetaData", metadata)).ConfigureAwait(false);
        await InvokeAsync(renderer, "Play", cancellationToken, ("InstanceID", "0"), ("Speed", "1")).ConfigureAwait(false);
    }

    public Task StopAsync(RendererInfo renderer, CancellationToken cancellationToken = default) =>
        InvokeAsync(renderer, "Stop", cancellationToken, ("InstanceID", "0"));

    /// <summary>CurrentTransportState, or null when the renderer did not answer.</summary>
    public async Task<string?> GetTransportStateAsync(RendererInfo renderer, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await InvokeAsync(renderer, "GetTransportInfo", cancellationToken, ("InstanceID", "0")).ConfigureAwait(false);
            return result.TryGetValue("CurrentTransportState", out var state) ? state : null;
        }
        catch (UpnpException ex)
        {
            Log.Warn($"GetTransportInfo failed on {renderer.FriendlyName}: {ex.Code} {ex.Message}");
            return null;
        }
    }

    public Task<IReadOnlyDictionary<string, string>> GetPositionInfoAsync(RendererInfo renderer, CancellationToken cancellationToken = default) =>
        InvokeAsync(renderer, "GetPositionInfo", cancellationToken, ("InstanceID", "0"));

    public void Dispose() => _http.Dispose();

    private async Task<IReadOnlyDictionary<string, string>> InvokeAsync(RendererInfo renderer, string action,
        CancellationToken cancellationToken, params (string Name, string Value)[] arguments)
    {
        var body = Soap.Request(ServiceType, action, arguments);
        using var request = new HttpRequestMessage(HttpMethod.Post, renderer.AvTransportControl)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=\"utf-8\"");
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{ServiceType}#{action}\"");
        request.Headers.UserAgent.ParseAdd("OpenVideoSmartShare/1.0");

        Log.Info($"AVTransport {action} -> {renderer.FriendlyName}");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new UpnpException(0, ex.Message);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = Soap.ParseResponse(text);
                if (!response.IsSuccessStatusCode)
                {
                    throw new UpnpException((int)response.StatusCode, response.ReasonPhrase ?? "HTTP error");
                }

                return result;
            }
            catch (System.Xml.XmlException)
            {
                throw new UpnpException((int)response.StatusCode, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            catch (UpnpException ex)
            {
                Log.Warn($"AVTransport {action} failed on {renderer.FriendlyName}: {ex.Code} {ex.Message}");
                throw;
            }
        }
    }
}
