using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading;

namespace SBridge.Sisr;

[SupportedOSPlatform("windows")]
internal static class OwnedSisrHttp
{
    public static HttpClient Create(IOwnedSisrProcess process, IPEndPoint endpoint)
    {
        if (!IPAddress.IsLoopback(endpoint.Address)) throw new IOException("SISR API must be a verified owned loopback listener.");
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
                    // Ownership is checked after connecting, before transmitting
                    // any HTTP. Every pool belongs to one retained root/endpoint.
                    if (process.HasExited || !WindowsTcpListeners.IsOwned(process.Id, endpoint))
                        throw new IOException("The API listener does not belong to the retained SISR process.");
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler)
        {
            BaseAddress = new UriBuilder("http", endpoint.Address.ToString(), endpoint.Port).Uri,
            Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 64 * 1024
        };
    }
}
