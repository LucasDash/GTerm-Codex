using System.Net;

namespace GTerm
{
    internal static class HttpPeer
    {
        internal static bool IsLoopback(IPEndPoint? endpoint)
        {
            IPAddress? address = endpoint?.Address;
            return address != null && IPAddress.IsLoopback(
                address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
        }
    }
}
