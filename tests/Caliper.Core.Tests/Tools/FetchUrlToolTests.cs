// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net;
using System.Text;
using System.Text.Json;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Caliper.Core.Tools;
using Caliper.Core.Tools.BuiltIn;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Tools;

public sealed class FetchUrlToolTests
{
    [Fact]
    public async Task Extracts_clean_text_from_html()
    {
        var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Html("<html><body><nav>Menu</nav><main><h1>Hello</h1><script>x()</script><p>World</p></main></body></html>"),
        });
        var tool = BuildTool(maxChars: 1000);

        var result = await Invoke(tool, handler, "https://example.com/page");

        Assert.True(result.Success);
        Assert.Contains("Hello", result.Output, StringComparison.Ordinal);
        Assert.Contains("World", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Menu", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("x()", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_disallowed_mime()
    {
        var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var tool = BuildTool(maxChars: 1000);

        var result = await Invoke(tool, handler, "https://example.com/data");

        Assert.False(result.Success);
        Assert.Contains("Unsupported content type", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_pdf_without_text_extraction()
    {
        var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x25, 0x50, 0x44, 0x46])
            {
                Headers = { ContentType = new("application/pdf") },
            },
        });
        var tool = BuildTool(maxChars: 1000);

        var result = await Invoke(tool, handler, "https://example.com/file.pdf");

        Assert.False(result.Success);
        Assert.Contains("Unsupported content type: application/pdf", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_redirect_to_private_address()
    {
        var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://127.0.0.1/private") },
        });
        var tool = BuildTool(maxChars: 1000);

        var result = await Invoke(tool, handler, "https://example.com/redirect");

        Assert.False(result.Success);
        Assert.Contains("Blocked private", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncates_output_to_configured_cap()
    {
        var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Html($"<html><body><main>{new string('a', 500)}</main></body></html>"),
        });
        var tool = BuildTool(maxChars: 80);

        var result = await Invoke(tool, handler, "https://example.com/long");

        Assert.True(result.Success);
        Assert.True(result.Output.Length <= 80);
        Assert.EndsWith("[truncated]", result.Output, StringComparison.Ordinal);
    }

    private static FetchUrlTool BuildTool(int maxChars) =>
        new(Options.Create(new CaliperOptions
        {
            Model = "test",
            ToolOutputMaxChars = maxChars,
            ToolTimeoutSeconds = 30,
        }));

    private static async Task<ToolResult> Invoke(FetchUrlTool tool, HttpMessageHandler handler, string url)
    {
        using var doc = JsonDocument.Parse($$"""{"url":"{{url}}"}""");
        var ctx = new ToolContext(
            new StaticHttpClientFactory(new HttpClient(handler)),
            NullLogger.Instance,
            ".",
            ".",
            false,
            CancellationToken.None);
        return await tool.InvokeAsync(doc.RootElement, ctx, CancellationToken.None);
    }

    private static StringContent Html(string html) =>
        new(html, Encoding.UTF8, "text/html");
}

file sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(_responses.Dequeue());
}

file sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
