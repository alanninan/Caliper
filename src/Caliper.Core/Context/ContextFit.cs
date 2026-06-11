// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;

namespace Caliper.Core.Context;

public sealed record ContextFit(
    IReadOnlyList<ChatMessage> Messages,
    bool Compacted,
    int? BeforeTokens,
    int? AfterTokens,
    int? EstimatedPromptTokens,
    int? RawEstimatedPromptTokens = null,
    int ActiveStartIndex = 0);
