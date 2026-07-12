// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Configuration;
using Caliper.Core.Tools;
using Caliper.Core.Tools.BuiltIn;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Tools;

public sealed class LocalToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "caliper-tools-" + Guid.NewGuid().ToString("N"));

    public LocalToolTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task File_tools_cover_read_list_glob_grep_write_and_edit()
    {
        var ctx = Context();
        var options = Options.Create(new CaliperOptions { ToolOutputMaxChars = 4000 });
        var write = await new WriteFileTool().InvokeAsync(JsonSerializer.SerializeToElement(new { path = "docs/a.txt", content = "alpha\nbeta\nalpha" }), ctx, CancellationToken.None);
        Assert.True(write.Success);
        Assert.NotNull(write.FileChange);
        Assert.Empty(write.FileChange.Before);
        Assert.Equal("alpha\nbeta\nalpha", write.FileChange.After);

        var read = await new ReadFileTool(options).InvokeAsync(JsonSerializer.SerializeToElement(new { path = "docs/a.txt", start_line = 2, end_line = 2 }), ctx, CancellationToken.None);
        Assert.Equal("2: beta", read.Output);

        var list = await new ListDirTool(options).InvokeAsync(JsonSerializer.SerializeToElement(new { path = "." }), ctx, CancellationToken.None);
        Assert.Contains("docs/", list.Output, StringComparison.Ordinal);

        var glob = await new GlobTool(options).InvokeAsync(JsonSerializer.SerializeToElement(new { path = ".", pattern = "**/*.txt" }), ctx, CancellationToken.None);
        Assert.Contains("docs", glob.Output, StringComparison.Ordinal);

        var grep = await new GrepTool(options).InvokeAsync(JsonSerializer.SerializeToElement(new { path = ".", pattern = "beta" }), ctx, CancellationToken.None);
        Assert.Contains("beta", grep.Output, StringComparison.Ordinal);

        var edit = await new EditFileTool().InvokeAsync(JsonSerializer.SerializeToElement(new { path = "docs/a.txt", old_str = "beta", new_str = "gamma" }), ctx, CancellationToken.None);
        Assert.True(edit.Success);
        Assert.NotNull(edit.FileChange);
        Assert.Contains("beta", edit.FileChange.Before, StringComparison.Ordinal);
        Assert.Contains("gamma", edit.FileChange.After, StringComparison.Ordinal);
        Assert.Contains("gamma", File.ReadAllText(Path.Combine(_root, "docs", "a.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_file_preserves_utf8_bom()
    {
        var ctx = Context();
        var path = Path.Combine(_root, "bom.txt");
        var utf8Bom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllTextAsync(path, "alpha beta", utf8Bom);

        var edit = await new EditFileTool().InvokeAsync(
            JsonSerializer.SerializeToElement(new { path = "bom.txt", old_str = "beta", new_str = "gamma" }),
            ctx,
            CancellationToken.None);

        Assert.True(edit.Success);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("alpha gamma", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Edit_file_preserves_utf16le_encoding()
    {
        var ctx = Context();
        var path = Path.Combine(_root, "utf16.txt");
        await File.WriteAllTextAsync(path, "alpha beta", System.Text.Encoding.Unicode);

        var edit = await new EditFileTool().InvokeAsync(
            JsonSerializer.SerializeToElement(new { path = "utf16.txt", old_str = "beta", new_str = "gamma" }),
            ctx,
            CancellationToken.None);

        Assert.True(edit.Success);
        var bytes = await File.ReadAllBytesAsync(path);
        // UTF-16LE BOM (FF FE) must survive, and the content must decode as UTF-16, not mojibake.
        Assert.Equal([0xFF, 0xFE], bytes[..2]);
        Assert.Equal("alpha gamma", await File.ReadAllTextAsync(path, System.Text.Encoding.Unicode));
    }

    [Fact]
    public async Task Write_file_new_file_defaults_to_bomless_utf8()
    {
        var ctx = Context();

        var write = await new WriteFileTool().InvokeAsync(
            JsonSerializer.SerializeToElement(new { path = "plain.txt", content = "hello" }),
            ctx,
            CancellationToken.None);

        Assert.True(write.Success);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, "plain.txt"));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("hello"), bytes);
    }

    [Fact]
    public async Task Edit_file_rejects_zero_and_multiple_matches()
    {
        var ctx = Context();
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x x");
        var tool = new EditFileTool();

        var zero = await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { path = "a.txt", old_str = "y", new_str = "z" }), ctx, CancellationToken.None);
        var many = await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { path = "a.txt", old_str = "x", new_str = "z" }), ctx, CancellationToken.None);

        Assert.False(zero.Success);
        Assert.False(many.Success);
    }

    [Fact]
    public async Task File_tool_rejects_escape_when_gate_has_not_authorized_outside_access()
    {
        var outside = Path.Combine(Path.GetTempPath(), "caliper-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "secret");
        try
        {
            var read = await new ReadFileTool(Options.Create(new CaliperOptions())).InvokeAsync(
                JsonSerializer.SerializeToElement(new { path = outside }),
                Context(allowOutside: false),
                CancellationToken.None);

            Assert.False(read.Success);
            Assert.Contains("outside the working root", read.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task File_tool_honors_outside_access_after_gate_authorization()
    {
        var outside = Path.Combine(Path.GetTempPath(), "caliper-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "authorized");
        try
        {
            var read = await new ReadFileTool(Options.Create(new CaliperOptions())).InvokeAsync(
                JsonSerializer.SerializeToElement(new { path = outside }),
                Context(allowOutside: true),
                CancellationToken.None);

            Assert.True(read.Success);
            Assert.Equal("1: authorized", read.Output);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Shell_tool_captures_output()
    {
        var shell = OperatingSystem.IsWindows() ? "powershell" : "bash";
        var command = OperatingSystem.IsWindows() ? "Write-Output hello" : "echo hello";
        var result = await new ShellTool(Options.Create(new CaliperOptions { ToolOutputMaxChars = 1000 }), shell)
            .InvokeAsync(JsonSerializer.SerializeToElement(new { command }), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("hello", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private ToolContext Context(bool allowOutside = false) =>
        new(new NullHttpClientFactory(), NullLogger.Instance, ".", _root, allowOutside, CancellationToken.None);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

file sealed class NullHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
