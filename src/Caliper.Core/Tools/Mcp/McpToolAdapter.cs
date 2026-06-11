// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Caliper.Core.Tools.Mcp;

public sealed class McpToolAdapter(
    string serverName,
    McpClientTool tool,
    IOptions<CaliperOptions> options) : IMcpTool
{
    public string Name { get; } = McpToolNaming.Namespaced(serverName, tool.ProtocolTool.Name);
    public string Description { get; } = tool.Description ?? $"MCP tool {tool.ProtocolTool.Name} from {serverName}.";
    public JsonElement ParameterSchema { get; } = tool.ProtocolTool.InputSchema.Clone();
    public SideEffect SideEffect { get; } = McpClassifier.Classify(tool.ProtocolTool.Annotations);

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            var result = await tool.CallAsync(ToDictionary(arguments), cancellationToken: ct).ConfigureAwait(false);
            var output = RenderResult(result);
            return new ToolResult(result.IsError != true, ToolOutput.Truncate(output, options.Value.ToolOutputMaxChars));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult(false, $"MCP tool '{Name}' failed: {ex.Message}");
        }
    }

    private static Dictionary<string, object?> ToDictionary(JsonElement arguments)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments.ValueKind != JsonValueKind.Object)
            return dictionary;

        foreach (var property in arguments.EnumerateObject())
            dictionary[property.Name] = ToObject(property.Value);
        return dictionary;
    }

    private static object? ToObject(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDouble(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => ToObject(property.Value),
                StringComparer.Ordinal),
            _ => element.Clone(),
        };

    private static string RenderResult(CallToolResult result)
    {
        var output = new StringBuilder();
        foreach (var block in result.Content ?? [])
        {
            switch (block)
            {
                case TextContentBlock text:
                    output.AppendLine(text.Text);
                    break;
                case ResourceLinkBlock link:
                    output.AppendLine($"resource: {link.Uri}");
                    break;
                case ImageContentBlock image:
                    output.AppendLine($"image: {image.MimeType} ({image.Data.Length} base64 chars)");
                    break;
                case AudioContentBlock audio:
                    output.AppendLine($"audio: {audio.MimeType} ({audio.Data.Length} base64 chars)");
                    break;
                default:
                    output.AppendLine(block.GetType().Name);
                    break;
            }
        }

        if (result.StructuredContent is { } structured)
            output.AppendLine(structured.GetRawText());

        return output.ToString().Trim();
    }
}
