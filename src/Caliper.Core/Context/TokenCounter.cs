// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.RegularExpressions;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

namespace Caliper.Core.Context;

internal sealed partial class TokenCounter : ITokenCounter
{
    private const int MessageOverheadTokens = 8;
    private readonly ILogger<TokenCounter> _logger;
    private readonly object _gate = new();
    private readonly Tokenizer? _tokenizer;
    private double _correctionFactor = 1.0;

    public TokenCounter(
        IOptions<TokenizerOptions> opts,
        ILogger<TokenCounter> logger)
    {
        _logger = logger;
        _tokenizer = TryLoadTokenizer(opts.Value);
    }

    public int Count(string text)
    {
        var raw = _tokenizer is not null
            ? Math.Max(1, _tokenizer.CountTokens(text))
            : CountHeuristic(text);

        lock (_gate)
        {
            return Math.Max(1, (int)Math.Ceiling(raw * _correctionFactor));
        }
    }

    public int Count(IEnumerable<ChatMessage> messages) =>
        messages.Sum(message =>
            MessageOverheadTokens
            + Count(message.Role.ToString())
            + Count(message.Kind.ToString())
            + Count(message.Content));

    public void Calibrate(int estimated, int actual)
    {
        if (estimated <= 0 || actual <= 0)
            return;

        var observed = Math.Clamp((double)actual / estimated, 0.25, 4.0);
        lock (_gate)
        {
            _correctionFactor = (0.8 * _correctionFactor) + (0.2 * observed);
        }
    }

    private Tokenizer? TryLoadTokenizer(TokenizerOptions opts)
    {
        if (opts.Kind == TokenizerKind.Heuristic)
            return null;

        var requiredFiles = opts.Kind switch
        {
            TokenizerKind.Bpe => [ResolvePath(opts.VocabPath), ResolvePath(opts.MergesPath)],
            TokenizerKind.Tiktoken => [ResolvePath(opts.ModelPath)],
            TokenizerKind.Llama => [ResolvePath(opts.ModelPath)],
            _ => Array.Empty<string>(),
        };

        foreach (var file in requiredFiles)
        {
            if (!File.Exists(file))
            {
                _logger.LogWarning(
                    "Tokenizer file '{TokenizerPath}' was not found; using heuristic token estimates.",
                    file);
                return null;
            }
        }

        try
        {
            return opts.Kind switch
            {
                TokenizerKind.Bpe => BpeTokenizer.Create(ResolvePath(opts.VocabPath), ResolvePath(opts.MergesPath)),
                TokenizerKind.Tiktoken => TiktokenTokenizer.Create(ResolvePath(opts.ModelPath), null, null, null, 0),
                TokenizerKind.Llama => CreateLlamaTokenizer(ResolvePath(opts.ModelPath)),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "{TokenizerKind} tokenizer could not be loaded: {Message}. Using heuristic token estimates.",
                opts.Kind,
                ex.Message);
            return null;
        }
    }

    private static LlamaTokenizer CreateLlamaTokenizer(string path)
    {
        using var stream = File.OpenRead(path);
        return LlamaTokenizer.Create(stream);
    }

    private static int CountHeuristic(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 1;

        var wordPieces = WordOrPunctuationRegex().Count(text);
        var charEstimate = (int)Math.Ceiling(text.Length / 4.0);
        return Math.Max(1, Math.Max(wordPieces, charEstimate));
    }

    private static string ResolvePath(string configuredPath) =>
        Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));

    [GeneratedRegex(@"\w+|[^\s\w]")]
    private static partial Regex WordOrPunctuationRegex();
}
