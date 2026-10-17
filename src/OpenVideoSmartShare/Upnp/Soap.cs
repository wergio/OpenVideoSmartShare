using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OpenVideoSmartShare.Upnp;

/// <summary>A parsed SOAP action invocation.</summary>
internal sealed record SoapRequest(string Action, IReadOnlyDictionary<string, string> Arguments)
{
    public string Get(string name, string fallback = "") =>
        Arguments.TryGetValue(name, out var value) ? value : fallback;

    public int GetInt(string name, int fallback = 0) =>
        int.TryParse(Get(name), out var value) ? value : fallback;
}

/// <summary>Raised by service handlers to produce a UPnP error response.</summary>
internal sealed class UpnpException(int code, string description) : Exception(description)
{
    public int Code { get; } = code;
}

internal static class Soap
{
    private const string EnvelopeStart =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
        "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>";

    private const string EnvelopeEnd = "</s:Body></s:Envelope>";

    /// <summary>Parses the body of a control request; returns null when it is not a SOAP envelope.</summary>
    public static SoapRequest? Parse(string body)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(body);
        }
        catch (XmlException)
        {
            return null;
        }

        var bodyElement = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body");
        var action = bodyElement?.Elements().FirstOrDefault();
        if (action == null)
        {
            return null;
        }

        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var arg in action.Elements())
        {
            args[arg.Name.LocalName] = arg.Value;
        }

        return new SoapRequest(action.Name.LocalName, args);
    }

    /// <summary>Builds a response envelope; argument values are escaped here.</summary>
    public static string Response(string serviceType, string action, params (string Name, string Value)[] arguments)
    {
        var sb = new StringBuilder(EnvelopeStart);
        sb.Append("<u:").Append(action).Append("Response xmlns:u=\"").Append(serviceType).Append("\">");
        foreach (var (name, value) in arguments)
        {
            sb.Append('<').Append(name).Append('>').Append(Didl.Escape(value)).Append("</").Append(name).Append('>');
        }

        sb.Append("</u:").Append(action).Append("Response>");
        return sb.Append(EnvelopeEnd).ToString();
    }

    /// <summary>Builds a request envelope (used by the AVTransport client).</summary>
    public static string Request(string serviceType, string action, params (string Name, string Value)[] arguments)
    {
        var sb = new StringBuilder(EnvelopeStart);
        sb.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(serviceType).Append("\">");
        foreach (var (name, value) in arguments)
        {
            sb.Append('<').Append(name).Append('>').Append(Didl.Escape(value)).Append("</").Append(name).Append('>');
        }

        sb.Append("</u:").Append(action).Append('>');
        return sb.Append(EnvelopeEnd).ToString();
    }

    public static string Fault(int code, string description) =>
        EnvelopeStart +
        "<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>" +
        "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + code + "</errorCode>" +
        "<errorDescription>" + Didl.Escape(description) + "</errorDescription></UPnPError></detail></s:Fault>" +
        EnvelopeEnd;

    /// <summary>Reads the action response arguments of a SOAP reply, or throws for a UPnP fault.</summary>
    public static IReadOnlyDictionary<string, string> ParseResponse(string body)
    {
        var doc = XDocument.Parse(body);
        var bodyElement = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body")
            ?? throw new UpnpException(0, "Invalid SOAP response");
        var first = bodyElement.Elements().FirstOrDefault() ?? throw new UpnpException(0, "Empty SOAP response");
        if (first.Name.LocalName == "Fault")
        {
            var error = first.Descendants().FirstOrDefault(e => e.Name.LocalName == "UPnPError");
            var code = int.TryParse(error?.Elements().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value, out var c) ? c : 0;
            var description = error?.Elements().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value
                ?? first.Elements().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value ?? "SOAP fault";
            throw new UpnpException(code, description);
        }

        return first.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value, StringComparer.Ordinal);
    }
}
