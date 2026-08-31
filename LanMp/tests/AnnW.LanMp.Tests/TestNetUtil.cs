using System.Net;
using System.Net.Sockets;

namespace AnnW.LanMp.Tests
{
    internal static class TestNetUtil
    {
        /// <summary>Ask OS for a free loopback TCP port (avoids flaky random-range collisions).</summary>
        internal static int AllocateLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
