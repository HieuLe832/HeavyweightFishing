using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Chat.Api;

/// <summary>
/// The reverse proxies whose <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are believed. Behind a proxy the connection's address is
/// the proxy's, which would end up in sessions and the audit log and put every user in one auth rate-limit bucket. Empty (the default)
/// means no proxy: the headers are ignored, since anyone can send them.
/// </summary>
public class ProxyOptions
{
    public const string Section = "ForwardedHeaders";

    /// <summary>Proxy addresses, such as <c>10.0.0.5</c>.</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Proxy networks in CIDR form, such as <c>10.0.0.0/8</c>.</summary>
    public string[] KnownNetworks { get; set; } = [];

    public bool IsEmpty => KnownProxies.Length == 0 && KnownNetworks.Length == 0;

    public void ApplyTo(ForwardedHeadersOptions o)
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Clear();
        foreach (var proxy in KnownProxies) o.KnownProxies.Add(IPAddress.Parse(proxy));
        foreach (var network in KnownNetworks) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
}
