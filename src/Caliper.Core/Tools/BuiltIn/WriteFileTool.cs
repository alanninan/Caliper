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
            // Overwriting an existing file preserves its encoding/BOM; a brand-new file defaults to
            // BOM-less UTF-8.
            var before = string.Empty;
            System.Text.Encoding encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            if (File.Exists(path))
                (before, encoding) = await FileToolHelpers.ReadTextWithEncodingAsync(path, ct).ConfigureAwait(false);
            var content = FileToolHelpers.GetString(arguments, "content") ?? "";
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ctx.WorkingRoot);
            await File.WriteAllTextAsync(path, content, encoding, ct).ConfigureAwait(false);
            return new ToolResult(true, $"Wrote {path}", FileChange.Capture(path, before, content));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ToolResult(false, ex.Message);
        }
    }
}
