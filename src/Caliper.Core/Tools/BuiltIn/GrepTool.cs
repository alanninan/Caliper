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

public sealed class GrepTool(IOptions<CaliperOptions> options) : ITool
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["pattern"],"properties":{"pattern":{"type":"string"},"path":{"type":"string"},"glob":{"type":"string"},"case_sensitive":{"type":"boolean"}}}""").RootElement.Clone();

    public string Name => "grep";
    public string Description => "Search text files with a regular expression.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => SideEffect.ReadOnly;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            var root = FileToolHelpers.ResolvePath(FileToolHelpers.GetString(arguments, "path", ".") ?? ".", ctx);
            var caseSensitive = FileToolHelpers.GetBool(arguments, "case_sensitive");
            var regexOptions = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            var regex = new Regex(
                FileToolHelpers.GetString(arguments, "pattern") ?? "",
                regexOptions,
                RegexTimeout());
            var glob = FileToolHelpers.GetString(arguments, "glob");
            var files = SafeFileTraversal.EnumerateFiles(root, ct);

            var baseRoot = File.Exists(root) ? Path.GetDirectoryName(root) ?? root : root;
            if (!string.IsNullOrWhiteSpace(glob))
            {
                // A bare pattern like "*.cs" matches by name anywhere; a pattern with a slash is
                // matched against the path relative to the search root.
                var globRegex = GlobMatcher.ToRegex(glob.Contains('/', StringComparison.Ordinal) ? glob : "**/" + glob);
                files = files.Where(file =>
                    globRegex.IsMatch(Path.GetRelativePath(baseRoot, file).Replace('\\', '/')));
            }

            var sb = new StringBuilder();
            var count = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsBinaryAsync(file, ct).ConfigureAwait(false))
                    continue;

                var lineNo = 0;
                await foreach (var line in ReadLinesAsync(file, ct).ConfigureAwait(false))
                {
                    lineNo++;
                    if (!regex.IsMatch(line)) continue;
                    sb.AppendLine($"{Path.GetRelativePath(baseRoot, file)}:{lineNo}: {line}");
                    if (++count >= 200)
                        return new ToolResult(true, ToolOutput.Truncate(sb.ToString(), options.Value.ToolOutputMaxChars));
                }
            }

            return new ToolResult(true, ToolOutput.Truncate(sb.ToString(), options.Value.ToolOutputMaxChars));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or RegexMatchTimeoutException)
        {
            return new ToolResult(false, ex.Message);
        }
    }

    private TimeSpan RegexTimeout() =>
        TimeSpan.FromSeconds(Math.Max(1, Math.Min(options.Value.ToolTimeoutSeconds, 5)));

    private static async Task<bool> IsBinaryAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            // A NUL byte in the leading block is a reliable, cheap signal for a binary file.
            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                yield break;
            yield return line;
        }
    }
}
