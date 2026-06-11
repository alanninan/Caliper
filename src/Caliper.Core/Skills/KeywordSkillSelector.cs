// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.RegularExpressions;
using Caliper.Core.Abstractions;
using Caliper.Core.Models;

namespace Caliper.Core.Skills;

internal sealed partial class KeywordSkillSelector : ISkillSelector
{
    private static readonly HashSet<string> s_stopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from",
        "how", "i", "in", "is", "it", "of", "on", "or", "that", "the",
        "this", "to", "use", "when", "with", "you", "your",
    };

    public Task<IReadOnlyList<string>> SelectAsync(
        string userMessage,
        IReadOnlyList<SkillMetadata> all,
        int max,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(userMessage) || all.Count == 0 || max <= 0)
            return Task.FromResult<IReadOnlyList<string>>([]);

        var userTerms = Tokenize(userMessage);
        if (userTerms.Count == 0)
            return Task.FromResult<IReadOnlyList<string>>([]);

        var selected = all
            .Select(skill => new
            {
                skill.Name,
                Score = Score(userTerms, Tokenize(skill.Description)),
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .Take(max)
            .Select(candidate => candidate.Name)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(selected);
    }

    private static double Score(HashSet<string> userTerms, HashSet<string> skillTerms)
    {
        if (skillTerms.Count == 0)
            return 0;

        var intersection = userTerms.Intersect(skillTerms, StringComparer.OrdinalIgnoreCase).Count();
        if (intersection == 0)
            return 0;

        var union = userTerms.Union(skillTerms, StringComparer.OrdinalIgnoreCase).Count();
        return (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string text) =>
        WordRegex()
            .Matches(text)
            .Select(match => match.Value.ToLowerInvariant())
            .Where(word => !s_stopWords.Contains(word))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("[a-zA-Z0-9]+")]
    private static partial Regex WordRegex();
}
