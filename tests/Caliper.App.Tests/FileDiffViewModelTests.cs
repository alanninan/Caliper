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

    // A3: a diff with only a handful of lines must keep each template's historical fixed width —
    // 36 for the side-by-side number columns, 40 for the inline one — so small diffs render exactly
    // as they did before this change.
    [Fact]
    public void Create_small_change_keeps_minimum_line_number_column_width()
    {
        var diff = FileDiffViewModel.Create(new FileChange(
            "sample.txt",
            "one\ntwo\n",
            "one\nthree\n"));

        Assert.All(diff.SideBySideRows, row => Assert.Equal(36, row.LineNumberColumnWidth));
        Assert.All(diff.InlineRows, row => Assert.Equal(40, row.LineNumberColumnWidth));
    }

    // A3: a change whose line numbers pass 9,999 (5 digits) must widen both templates' number
    // columns beyond their minimums, or the number clips.
    [Fact]
    public void Create_change_past_9999_lines_widens_line_number_column_width()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 10_050).Select(i => $"line{i}"));
        var before = lines + "\n";
        var after = lines.Replace("line5000", "changed5000", StringComparison.Ordinal) + "\n";

        var diff = FileDiffViewModel.Create(new FileChange("large.txt", before, after));

        Assert.All(diff.SideBySideRows, row => Assert.True(row.LineNumberColumnWidth > 36));
        Assert.All(diff.InlineRows, row => Assert.True(row.LineNumberColumnWidth > 40));
    }

    // A3: once the digit-driven width exceeds both templates' minimums (36 and 40), side-by-side
    // and inline must land on the exact same column width — proof they're driven by one shared
    // digit count rather than two independently-tuned formulas that could drift apart.
    [Fact]
    public void Create_change_past_9999_lines_widens_side_by_side_and_inline_equally()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 10_050).Select(i => $"line{i}"));
        var before = lines + "\n";
        var after = lines.Replace("line5000", "changed5000", StringComparison.Ordinal) + "\n";

        var diff = FileDiffViewModel.Create(new FileChange("large.txt", before, after));

        var sideBySideWidth = diff.SideBySideRows[0].LineNumberColumnWidth;
        var inlineWidth = diff.InlineRows[0].LineNumberColumnWidth;
        Assert.Equal(sideBySideWidth, inlineWidth);
        Assert.All(diff.SideBySideRows, row => Assert.Equal(sideBySideWidth, row.LineNumberColumnWidth));
        Assert.All(diff.InlineRows, row => Assert.Equal(inlineWidth, row.LineNumberColumnWidth));
    }
}
