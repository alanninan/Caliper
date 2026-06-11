// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tools.BuiltIn;

public sealed class ListDirTool(IOptions<CaliperOptions> options) : ITool
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"properties":{"path":{"type":"string"}}}""").RootElement.Clone();

    public string Name => "list_dir";
    public string Description => "List files and directories.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => SideEffect.ReadOnly;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            var path = FileToolHelpers.ResolvePath(FileToolHelpers.GetString(arguments, "path", ".") ?? ".", ctx);
            if (!Directory.Exists(path))
                return Task.FromResult(new ToolResult(false, $"Directory not found: {path}"));

            var entries = Directory.EnumerateFileSystemEntries(path)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(entry => Directory.Exists(entry) ? $"{Path.GetFileName(entry)}/" : Path.GetFileName(entry));
            return Task.FromResult(new ToolResult(true, ToolOutput.Truncate(string.Join(Environment.NewLine, entries), options.Value.ToolOutputMaxChars)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Task.FromResult(new ToolResult(false, ex.Message));
        }
    }
}
