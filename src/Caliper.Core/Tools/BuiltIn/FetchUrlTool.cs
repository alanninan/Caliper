// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tools.BuiltIn;

public sealed partial class FetchUrlTool(IOptions<CaliperOptions> opts) : ITool
{
    private const string ClientName = "fetch_url";
    private const int MaxRedirects = 3;
    private const int MaxBytes = 1_000_000;
    private readonly UrlSafetyGuard _guard = new();

    private static readonly JsonElement ParameterSchemaValue =
        JsonDocument.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["url"],
              "properties": { "url": { "type": "string", "maxLength": 2048 } }
            }
            """).RootElement.Clone();

    public string Name => "fetch_url";
    public string Description => "Fetch a public web page by URL and extract readable text. Use after search to inspect a result.";
    public JsonElement ParameterSchema => ParameterSchemaValue;
    public SideEffect SideEffect => SideEffect.Network;

    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments,
        ToolContext ctx,
        CancellationToken ct)
    {
        if (!arguments.TryGetProperty("url", out var urlEl))
            return new ToolResult(false, "Missing required argument: url");

        var url = urlEl.GetString();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new ToolResult(false, "Argument 'url' must be an absolute URL.");

        var safety = await _guard.GetUnsafeReasonAsync(uri, ct).ConfigureAwait(false);
        if (safety is not null)
            return new ToolResult(false, safety);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(opts.Value.ToolTimeoutSeconds));

        try
        {
            var response = await FetchWithRedirectsAsync(ctx.HttpClientFactory.CreateClient(ClientName), uri, timeout.Token)
                .ConfigureAwait(false);
            using (response)
            {
                var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "application/octet-stream";
                if (!IsAllowedMime(contentType))
                    return new ToolResult(false, $"Unsupported content type: {contentType}");

                var bytes = await ReadLimitedAsync(response.Content, MaxBytes, timeout.Token).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(bytes);
                if (contentType == "text/html")
                    text = ExtractHtmlText(text);

                text = CollapseWhitespace(text);
                return new ToolResult(true, ToolOutput.Truncate(text, opts.Value.ToolOutputMaxChars));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ToolResult(false, $"fetch_url timed out after {opts.Value.ToolTimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            return new ToolResult(false, ex.Message);
        }
    }

    private async Task<HttpResponseMessage> FetchWithRedirectsAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        var current = uri;
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                response.EnsureSuccessStatusCode();
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new HttpRequestException("Redirect response did not include a Location header.");

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            var safety = await _guard.GetUnsafeReasonAsync(current, ct).ConfigureAwait(false);
            if (safety is not null)
                throw new HttpRequestException(safety);
        }

        throw new HttpRequestException($"Too many redirects; limit is {MaxRedirects}.");
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                break;

            var allowed = Math.Min(read, maxBytes - (int)output.Length);
            if (allowed > 0)
                output.Write(buffer, 0, allowed);

            if (output.Length >= maxBytes)
                break;
        }

        return output.ToArray();
    }

    private static string ExtractHtmlText(string html)
    {
        html = StripBlockRegex().Replace(html, " ");
        html = TagRegex().Replace(html, " ");
        return System.Net.WebUtility.HtmlDecode(html);
    }

    private static bool IsAllowedMime(string mediaType) =>
        mediaType is "text/html" or "text/plain";

    private static bool IsRedirect(System.Net.HttpStatusCode status) =>
        (int)status is >= 300 and <= 399;

    private static string CollapseWhitespace(string text) =>
        WhitespaceRegex().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"<\s*(script|style|nav|header|footer|aside)\b[^>]*>.*?<\s*/\s*\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex StripBlockRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();
}
