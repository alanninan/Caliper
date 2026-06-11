// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net;
using Caliper.Core.Tools;

namespace Caliper.Core.Tests.Tools;

public sealed class UrlSafetyGuardTests
{
    [Fact]
    public async Task Rejects_non_http_schemes()
    {
        var guard = new UrlSafetyGuard(ResolveTo(IPAddress.Parse("93.184.216.34")));

        var reason = await guard.GetUnsafeReasonAsync(new Uri("file:///etc/passwd"), CancellationToken.None);

        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.1.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("ff02::1")]
    [InlineData("fc00::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    public async Task Rejects_private_loopback_and_link_local_addresses(string address)
    {
        var guard = new UrlSafetyGuard(ResolveTo(IPAddress.Parse(address)));

        var reason = await guard.GetUnsafeReasonAsync(new Uri("https://example.com"), CancellationToken.None);

        Assert.NotNull(reason);
    }

    [Fact]
    public async Task Allows_public_address()
    {
        var guard = new UrlSafetyGuard(ResolveTo(IPAddress.Parse("93.184.216.34")));

        var reason = await guard.GetUnsafeReasonAsync(new Uri("https://example.com"), CancellationToken.None);

        Assert.Null(reason);
    }

    private static Func<string, CancellationToken, Task<IPAddress[]>> ResolveTo(IPAddress address) =>
        (_, _) => Task.FromResult(new[] { address });
}
