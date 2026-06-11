// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Configuration;

public sealed class TokenizerOptions
{
    public TokenizerKind Kind { get; set; } = TokenizerKind.Heuristic;
    public string ModelPath { get; set; } = "./tokenizers/tokenizer.json";
    public string VocabPath { get; set; } = "./tokenizers/vocab.json";
    public string MergesPath { get; set; } = "./tokenizers/merges.txt";
    public string ModelName { get; set; } = "local-model";
}
