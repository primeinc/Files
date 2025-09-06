using System.Net;
using System.Net.Sockets;

namespace Files.App.Communication
{
    public static class EphemeralPortAllocator
    {
        public static int GetEphemeralTcpPort()
        {
            // Bind to port 0 to have OS assign an ephemeral port, then release immediately
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
