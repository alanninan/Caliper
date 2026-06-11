// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net;
using System.Net.Sockets;

namespace Caliper.Core.Tools;

internal sealed class UrlSafetyGuard(
    Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolver =
        resolver ?? ResolveDnsAsync;

    public async Task<string?> GetUnsafeReasonAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme is not ("http" or "https"))
            return $"Unsupported URL scheme: {uri.Scheme}";

        IPAddress[] addresses;
        try
        {
            addresses = await _resolver(uri.Host, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            return $"Could not resolve host '{uri.Host}': {ex.Message}";
        }

        if (addresses.Length == 0)
            return $"Could not resolve host '{uri.Host}'.";

        return addresses.Any(IsBlockedAddress)
            ? $"Blocked private, loopback, or link-local address for host '{uri.Host}'."
            : null;
    }

    private static Task<IPAddress[]> ResolveDnsAsync(string host, CancellationToken ct) =>
        Dns.GetHostAddressesAsync(host, ct);

    internal static bool IsBlockedAddress(IPAddress address)
    {
        // Normalize IPv4-mapped IPv6 (e.g. ::ffff:127.0.0.1) so the IPv4 ranges below
        // are checked — otherwise a mapped private/loopback address slips through.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                0 => true,
                10 => true,
                127 => true,
                100 when bytes[1] is >= 64 and <= 127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] is >= 16 and <= 31 => true,
                192 when bytes[1] == 168 => true,
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || bytes.All(static b => b == 0)
                || bytes[0] == 0xfc
                || bytes[0] == 0xfd;
        }

        return true;
    }
}
