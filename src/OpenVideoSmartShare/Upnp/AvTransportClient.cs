using System.Net.Http.Headers;
using System.Text;
using OpenVideoSmartShare.Infrastructure;

namespace OpenVideoSmartShare.Upnp;

/// <summary>
/// AVTransport:1 control point. Playback is Stop (when something is loaded),
/// SetAVTransportURI with the DIDL-Lite metadata, then Play(Speed=1). Volume goes through
/// the RenderingControl:1 service.
/// </summary>
internal sealed class AvTransportClient : IDisposable
{
    public const string ServiceType = "urn:schemas-upnp-org:service:AVTransport:1";
    public const string RenderingControlType = "urn:schemas-upnp-org:service:RenderingControl:1";

    private const int TransitionNotAvailable = 701;

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

        try
        {
            await SetUriAsync(renderer, uri, metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (UpnpException ex) when (ex.Code == TransitionNotAvailable)
        {
            // The TV can be stuck on a previous error while reporting an idle state: Stop resets it.
            Log.Warn($"{renderer.FriendlyName} refused the new video (state {state ?? "unknown"}): stop and retry");
            try
            {
                await StopAsync(renderer, cancellationToken).ConfigureAwait(false);
            }
            catch (UpnpException stopError)
            {
                Log.Warn($"Stop before retry failed on {renderer.FriendlyName}: {stopError.Code} {stopError.Message}");
            }

            await SetUriAsync(renderer, uri, metadata, cancellationToken).ConfigureAwait(false);
        }

        await InvokeAsync(renderer, "Play", cancellationToken, ("InstanceID", "0"), ("Speed", "1")).ConfigureAwait(false);
    }

    private Task SetUriAsync(RendererInfo renderer, string uri, string metadata, CancellationToken cancellationToken) =>
        InvokeAsync(renderer, "SetAVTransportURI", cancellationToken,
            ("InstanceID", "0"), ("CurrentURI", uri), ("CurrentURIMetaData", metadata));

    public Task StopAsync(RendererInfo renderer, CancellationToken cancellationToken = default) =>
        InvokeAsync(renderer, "Stop", cancellationToken, ("InstanceID", "0"));

    public Task PauseAsync(RendererInfo renderer, CancellationToken cancellationToken = default) =>
        InvokeAsync(renderer, "Pause", cancellationToken, ("InstanceID", "0"));

    /// <summary>Resumes a paused video.</summary>
    public Task ResumeAsync(RendererInfo renderer, CancellationToken cancellationToken = default) =>
        InvokeAsync(renderer, "Play", cancellationToken, ("InstanceID", "0"), ("Speed", "1"));

    /// <summary>Master volume (usually 0-100).</summary>
    public async Task<int> GetVolumeAsync(RendererInfo renderer, CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(renderer, RenderingControlType, "GetVolume", cancellationToken,
            ("InstanceID", "0"), ("Channel", "Master")).ConfigureAwait(false);
        return result.TryGetValue("CurrentVolume", out var text) && int.TryParse(text, out var volume)
            ? volume
            : throw new UpnpException(0, "No CurrentVolume in the response");
    }

    public async Task<bool> GetMuteAsync(RendererInfo renderer, CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(renderer, RenderingControlType, "GetMute", cancellationToken,
            ("InstanceID", "0"), ("Channel", "Master")).ConfigureAwait(false);
        return result.TryGetValue("CurrentMute", out var text)
            ? text.Trim() is "1" || string.Equals(text.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            : throw new UpnpException(0, "No CurrentMute in the response");
    }

    public Task SetMuteAsync(RendererInfo renderer, bool mute, CancellationToken cancellationToken = default) =>
        InvokeAsync(renderer, RenderingControlType, "SetMute", cancellationToken,
            ("InstanceID", "0"), ("Channel", "Master"), ("DesiredMute", mute ? "1" : "0"));

    public Task SetVolumeAsync(RendererInfo renderer, int volume, CancellationToken cancellationToken = default) =>
        InvokeAsync(renderer, RenderingControlType, "SetVolume", cancellationToken,
            ("InstanceID", "0"), ("Channel", "Master"), ("DesiredVolume", volume.ToString(System.Globalization.CultureInfo.InvariantCulture)));

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

    private Task<IReadOnlyDictionary<string, string>> InvokeAsync(RendererInfo renderer, string action,
        CancellationToken cancellationToken, params (string Name, string Value)[] arguments) =>
        InvokeAsync(renderer, ServiceType, action, cancellationToken, arguments);

    private async Task<IReadOnlyDictionary<string, string>> InvokeAsync(RendererInfo renderer, string serviceType, string action,
        CancellationToken cancellationToken, params (string Name, string Value)[] arguments)
    {
        var control = serviceType == ServiceType ? renderer.AvTransportControl
            : renderer.RenderingControl ?? throw new UpnpException(0, "The TV has no RenderingControl service");
        var service = serviceType == ServiceType ? "AVTransport" : "RenderingControl";
        var body = Soap.Request(serviceType, action, arguments);
        using var request = new HttpRequestMessage(HttpMethod.Post, control)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=\"utf-8\"");
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{action}\"");
        request.Headers.UserAgent.ParseAdd("OpenVideoSmartShare/1.0");

        Log.Info($"{service} {action} -> {renderer.FriendlyName}");
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
                Log.Warn($"{service} {action} failed on {renderer.FriendlyName}: {ex.Code} {ex.Message}");
                throw;
            }
        }
    }
}
