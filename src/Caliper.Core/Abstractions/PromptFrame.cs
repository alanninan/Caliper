// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Models;

namespace Caliper.Core.Abstractions;

public sealed record PromptFrame(
    string SystemPrompt,
    IReadOnlyList<ChatMessage> History,
    IReadOnlyList<JsonElement> ToolSchemas);

public sealed record ContextBudget(
    int ContextWindowTokens,
    int ReservedOutputTokens,
    double CompactAtFraction,
    bool Force = false);
