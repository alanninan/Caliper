// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caliper.Core.Protocol;

/// <summary>
/// Discriminated union of all model turn types. Deserialized exclusively via
/// <see cref="AgentTurnConverter"/> — never by STJ polymorphism.
/// </summary>
[JsonConverter(typeof(AgentTurnConverter))]
public abstract record AgentTurn
{
    public required string Rationale { get; init; }
}

public sealed record RespondTurn : AgentTurn
{
    public required string Content { get; init; }
}

public sealed record CallToolTurn : AgentTurn
{
    public required string Tool      { get; init; }
    public required JsonElement Arguments { get; init; }
}

public sealed record LoadSkillTurn : AgentTurn
{
    public required string Skill { get; init; }
}
