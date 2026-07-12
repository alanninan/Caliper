// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels;
using Caliper.Core.Models;

namespace Caliper.App.Tests;

public sealed class FileDiffViewModelTests
{
    [Fact]
    public void Create_builds_aligned_and_inline_rows()
    {
        var diff = FileDiffViewModel.Create(new FileChange(
            "sample.txt",
            "one\ntwo\n",
            "one\nthree\n"));

        Assert.Equal("sample.txt", diff.Path);
        Assert.NotEmpty(diff.SideBySideRows);
        Assert.Contains(diff.InlineRows, line => line.Kind == DiffLineKind.Removed && line.Prefix == "-");
        Assert.Contains(diff.InlineRows, line => line.Kind == DiffLineKind.Added && line.Prefix == "+");
    }

    [Fact]
    public void Capture_large_change_marks_truncated()
    {
        var change = FileChange.Capture("large.txt", new string('a', 70_000), new string('b', 70_000));

        Assert.True(change.Truncated);
        Assert.Contains("[diff content truncated]", change.After, StringComparison.Ordinal);
    }
}
