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
