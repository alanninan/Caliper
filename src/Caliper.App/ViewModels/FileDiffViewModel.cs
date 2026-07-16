// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Globalization;
using Caliper.Core.Models;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Caliper.App.ViewModels;

public sealed class FileDiffViewModel
{
    // A3: ChatPage.xaml's SideBySideDiffTemplate/InlineDiffTemplate number columns used to be
    // fixed at 36/40 DIPs, which clips once a line number passes 9,999 (5+ digits). Both templates
    // show the number in CaptionTextBlockStyle (12px) + CodeFontFamily (monospace) with
    // Padding="4,2". PerDigitWidth (8) approximates that font's glyph advance width at 12px;
    // FixedPadding (12) covers the 4+4=8px horizontal Padding plus a small buffer against the
    // adjoining divider/column. At 3 digits the formula reproduces the original side-by-side
    // constant exactly (12 + 3*8 = 36), which is why 36 was the historical value; the two Min
    // constants below keep it as the floor so small diffs still measure exactly as before.
    private const double PerDigitWidth = 8;
    private const double FixedPadding = 12;
    private const double SideBySideMinColumnWidth = 36;
    private const double InlineMinColumnWidth = 40;

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
        var inline = InlineDiffBuilder.Instance.BuildDiffModel(change.Before, change.After);

        // A3: the column width is computed once per diff, from the largest line number across
        // both views, then stamped onto every row below — every row in a virtualized diff must
        // share the same width for the columns to stay aligned, so this can't be a per-row Auto size.
        var maxLineNumber = 0;
        foreach (var line in sideBySide.OldText.Lines)
            maxLineNumber = Math.Max(maxLineNumber, line.Position ?? 0);
        foreach (var line in sideBySide.NewText.Lines)
            maxLineNumber = Math.Max(maxLineNumber, line.Position ?? 0);
        foreach (var line in inline.Lines)
            maxLineNumber = Math.Max(maxLineNumber, line.Position ?? 0);

        var digits = Math.Max(1, maxLineNumber).ToString(CultureInfo.CurrentCulture).Length;
        var rawWidth = FixedPadding + (digits * PerDigitWidth);
        var sideBySideWidth = Math.Max(SideBySideMinColumnWidth, rawWidth);
        var inlineWidth = Math.Max(InlineMinColumnWidth, rawWidth);

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
                ToKind(newLine?.Type),
                sideBySideWidth));
        }

        var inlineRows = inline.Lines.Select(line =>
            new InlineDiffRowViewModel(
                line.Position,
                Prefix(line.Type),
                line.Text,
                ToKind(line.Type),
                inlineWidth)).ToList();
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

// A3: LineNumberColumnWidth defaults to each template's historical fixed width (36/40) so any
// row built without going through FileDiffViewModel.Create (e.g. a future direct construction)
// still renders exactly as before.
public sealed record SideBySideDiffRowViewModel(
    int? OldLineNumber,
    string OldText,
    DiffLineKind OldKind,
    int? NewLineNumber,
    string NewText,
    DiffLineKind NewKind,
    double LineNumberColumnWidth = 36);

public sealed record InlineDiffRowViewModel(
    int? LineNumber,
    string Prefix,
    string Text,
    DiffLineKind Kind,
    double LineNumberColumnWidth = 40);
