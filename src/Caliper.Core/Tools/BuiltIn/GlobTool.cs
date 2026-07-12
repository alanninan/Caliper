// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using System.Text.RegularExpressions;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tools.BuiltIn;

public sealed class GlobTool(IOptions<CaliperOptions> options) : ITool
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["pattern"],"properties":{"pattern":{"type":"string"},"path":{"type":"string"}}}""").RootElement.Clone();

    public string Name => "glob";
    public string Description => "Find files by glob pattern under a directory.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => SideEffect.ReadOnly;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            var root = FileToolHelpers.ResolvePath(FileToolHelpers.GetString(arguments, "path", ".") ?? ".", ctx);
            var pattern = FileToolHelpers.GetString(arguments, "pattern") ?? "*";
            if (!Directory.Exists(root))
                return Task.FromResult(new ToolResult(false, $"Directory not found: {root}"));

            var regex = GlobMatcher.ToRegex(pattern);
            var matches = SafeFileTraversal.EnumerateFiles(root, ct)
                .Select(path => Path.GetRelativePath(root, path))
                .Where(relative => regex.IsMatch(relative.Replace('\\', '/')))
                .Take(500);
            return Task.FromResult(new ToolResult(true, ToolOutput.Truncate(string.Join(Environment.NewLine, matches), options.Value.ToolOutputMaxChars)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or RegexMatchTimeoutException)
        {
            return Task.FromResult(new ToolResult(false, ex.Message));
        }
    }
}
