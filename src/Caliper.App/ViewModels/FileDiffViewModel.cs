// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Caliper.App.ViewModels;

public sealed class FileDiffViewModel
{
    private FileDiffViewModel(
        FileChange change,
        IReadOnlyList<SideBySideDiffRowViewModel> sideBySideRows,
        IReadOnlyList<InlineDiffRowViewModel> inlineRows)
    {
        Path = change.Path;
        IsTruncated = change.Truncated;
        SideBySideRows = sideBySideRows;
        InlineRows = inlineRows;
    }

    public string Path { get; }
    public bool IsTruncated { get; }
    public IReadOnlyList<SideBySideDiffRowViewModel> SideBySideRows { get; }
    public IReadOnlyList<InlineDiffRowViewModel> InlineRows { get; }

    public static FileDiffViewModel Create(FileChange change)
    {
        var sideBySide = SideBySideDiffBuilder.Instance.BuildDiffModel(change.Before, change.After);
        var count = Math.Max(sideBySide.OldText.Lines.Count, sideBySide.NewText.Lines.Count);
        var sideRows = new List<SideBySideDiffRowViewModel>(count);
        for (var index = 0; index < count; index++)
        {
            var oldLine = index < sideBySide.OldText.Lines.Count ? sideBySide.OldText.Lines[index] : null;
            var newLine = index < sideBySide.NewText.Lines.Count ? sideBySide.NewText.Lines[index] : null;
            sideRows.Add(new SideBySideDiffRowViewModel(
                oldLine?.Position,
                oldLine?.Text ?? string.Empty,
                ToKind(oldLine?.Type),
                newLine?.Position,
                newLine?.Text ?? string.Empty,
                ToKind(newLine?.Type)));
        }

        var inline = InlineDiffBuilder.Instance.BuildDiffModel(change.Before, change.After);
        var inlineRows = inline.Lines.Select(line =>
            new InlineDiffRowViewModel(
                line.Position,
                Prefix(line.Type),
                line.Text,
                ToKind(line.Type))).ToList();
        return new FileDiffViewModel(change, sideRows, inlineRows);
    }

    private static DiffLineKind ToKind(ChangeType? type) => type switch
    {
        ChangeType.Inserted => DiffLineKind.Added,
        ChangeType.Deleted => DiffLineKind.Removed,
        ChangeType.Modified => DiffLineKind.Modified,
        ChangeType.Imaginary => DiffLineKind.Empty,
        _ => DiffLineKind.Unchanged,
    };

    private static string Prefix(ChangeType type) => type switch
    {
        ChangeType.Inserted => "+",
        ChangeType.Deleted => "-",
        ChangeType.Modified => "~",
        _ => " ",
    };
}

public enum DiffLineKind { Unchanged, Added, Removed, Modified, Empty }

public sealed record SideBySideDiffRowViewModel(
    int? OldLineNumber,
    string OldText,
    DiffLineKind OldKind,
    int? NewLineNumber,
    string NewText,
    DiffLineKind NewKind);

public sealed record InlineDiffRowViewModel(
    int? LineNumber,
    string Prefix,
    string Text,
    DiffLineKind Kind);
