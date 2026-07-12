// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tools.BuiltIn;

public sealed class ReadFileTool(IOptions<CaliperOptions> options) : ITool
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["path"],"properties":{"path":{"type":"string"},"start_line":{"type":"integer"},"end_line":{"type":"integer"}}}""").RootElement.Clone();

    public string Name => "read_file";
    public string Description => "Read a text file, optionally by line range.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => SideEffect.ReadOnly;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            var path = FileToolHelpers.ResolvePath(FileToolHelpers.GetString(arguments, "path") ?? "", ctx);
            if (!File.Exists(path))
                return new ToolResult(false, $"File not found: {path}");

            var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
            var start = Math.Max(1, FileToolHelpers.GetInt(arguments, "start_line") ?? 1);
            var end = Math.Min(lines.Length, FileToolHelpers.GetInt(arguments, "end_line") ?? lines.Length);
            if (start > end)
                return new ToolResult(false, "start_line must be <= end_line.");

            // Prefix each line with its 1-based number so the model can cite and target ranges
            // precisely (mirrors grep's file:line output).
            var selected = lines
                .Skip(start - 1)
                .Take(end - start + 1)
                .Select((line, offset) => $"{start + offset}: {line}");
            return new ToolResult(true, ToolOutput.Truncate(string.Join(Environment.NewLine, selected), options.Value.ToolOutputMaxChars));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ToolResult(false, ex.Message);
        }
    }
}
