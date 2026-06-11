// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Models;

public sealed record RawModelCompletion(
    string RawJson,
    int PromptTokens,
    int OutputTokens);
