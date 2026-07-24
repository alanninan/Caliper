// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Caliper.Core.Memory;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Memory;

public sealed class CaliperMdProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "caliper-md-" + Guid.NewGuid().ToString("N"));

    public CaliperMdProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Present_project_file_is_read()
    {
        File.WriteAllText(Path.Combine(_root, "CALIPER.md"), "project context");
        var provider = Build();

        var document = await provider.ReadAsync(_root, CancellationToken.None);

        Assert.Equal("project context", document.Content);
        Assert.False(document.Truncated);
    }

    [Fact]
    public async Task Absent_project_file_returns_empty_document()
    {
        var provider = Build();

        var document = await provider.ReadAsync(_root, CancellationToken.None);

        Assert.Equal(string.Empty, document.Content);
        Assert.False(document.Truncated);
    }

    [Fact]
    public async Task Oversized_project_file_is_truncated()
    {
        File.WriteAllText(Path.Combine(_root, "CALIPER.md"), new string('x', 100));
        var provider = Build(maxChars: 40);

        var document = await provider.ReadAsync(_root, CancellationToken.None);

        Assert.True(document.Truncated);
        Assert.True(document.Content.Length <= 40);
        Assert.Contains(". [truncated]", document.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateIfMissingAsync_creates_default_heading()
    {
        var provider = Build();

        var document = await provider.CreateIfMissingAsync(_root, CancellationToken.None);

        Assert.Equal("# Project memory\n", document.Content);
        Assert.True(File.Exists(Path.Combine(_root, "CALIPER.md")));
    }

    [Fact]
    public async Task CreateIfMissingAsync_never_overwrites_existing_file()
    {
        File.WriteAllText(Path.Combine(_root, "CALIPER.md"), "keep me");
        var provider = Build();

        var document = await provider.CreateIfMissingAsync(_root, CancellationToken.None);

        Assert.Equal("keep me", document.Content);
    }

    [Fact]
    public async Task CreateIfMissingAsync_rejects_path_outside_working_root()
    {
        var provider = new CaliperMdProvider(Options.Create(new CaliperOptions
        {
            Memory = new MemoryOptions { ProjectFile = Path.Combine("..", "outside.md") },
        }));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => provider.CreateIfMissingAsync(_root, CancellationToken.None));
    }

    private static CaliperMdProvider Build(int maxChars = 4096) =>
        new(Options.Create(new CaliperOptions
        {
            ToolOutputMaxChars = maxChars,
            Memory = new MemoryOptions { ProjectFile = "CALIPER.md" },
        }));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
