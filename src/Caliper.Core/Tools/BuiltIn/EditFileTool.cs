// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Models;

namespace Caliper.Core.Tools.BuiltIn;

public sealed class EditFileTool : ITool
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["path","old_str","new_str"],"properties":{"path":{"type":"string"},"old_str":{"type":"string"},"new_str":{"type":"string"}}}""").RootElement.Clone();

    public string Name => "edit_file";
    public string Description => "Replace an exact string in a file; old_str must occur exactly once.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => SideEffect.Write;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            var path = FileToolHelpers.ResolvePath(FileToolHelpers.GetString(arguments, "path") ?? "", ctx);
            if (!File.Exists(path))
                return new ToolResult(false, $"File not found: {path}");

            var oldStr = FileToolHelpers.GetString(arguments, "old_str") ?? "";
            var newStr = FileToolHelpers.GetString(arguments, "new_str") ?? "";
            var (content, encoding) = await FileToolHelpers.ReadTextWithEncodingAsync(path, ct).ConfigureAwait(false);
            var count = CountOccurrences(content, oldStr);
            if (count != 1)
                return new ToolResult(false, $"old_str must occur exactly once; found {count} matches.");

            var updated = content.Replace(oldStr, newStr, StringComparison.Ordinal);
            // Preserve the file's original encoding/BOM so an edit doesn't re-encode the whole file.
            await File.WriteAllTextAsync(path, updated, encoding, ct).ConfigureAwait(false);
            return new ToolResult(true, $"Edited {path}", FileChange.Capture(path, content, updated));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ToolResult(false, ex.Message);
        }
    }

    private static int CountOccurrences(string text, string needle)
    {
        if (needle.Length == 0) return 0;
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
