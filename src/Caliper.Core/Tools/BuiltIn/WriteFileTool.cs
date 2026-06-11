// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Models;

namespace Caliper.Core.Tools.BuiltIn;

public sealed class WriteFileTool : ITool
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["path","content"],"properties":{"path":{"type":"string"},"content":{"type":"string"}}}""").RootElement.Clone();

    public string Name => "write_file";
    public string Description => "Create or overwrite a text file.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => SideEffect.Write;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            var path = FileToolHelpers.ResolvePath(FileToolHelpers.GetString(arguments, "path") ?? "", ctx);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ctx.WorkingRoot);
            await File.WriteAllTextAsync(path, FileToolHelpers.GetString(arguments, "content") ?? "", ct).ConfigureAwait(false);
            return new ToolResult(true, $"Wrote {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ToolResult(false, ex.Message);
        }
    }
}
