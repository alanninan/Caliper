// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Models;

namespace Caliper.Core.Agents;

public sealed record TurnContext(
    string System,
    IReadOnlyList<ChatMessage> Messages,
    IToolRegistry Tools,
    GenerationParameters Parameters,
    IReadOnlyList<string>? SkillMenu = null,
    string? Model = null);
