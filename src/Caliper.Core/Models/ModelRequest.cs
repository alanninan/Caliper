// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;

namespace Caliper.Core.Models;

public sealed record ModelRequest(
    string System,
    IReadOnlyList<ChatMessage> Messages,
    JsonElement ResponseSchema,
    GenerationParameters Parameters,
    bool ValidateAgentTurn = true);
