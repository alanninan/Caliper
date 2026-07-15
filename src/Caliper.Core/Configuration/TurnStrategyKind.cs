// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json.Serialization;

namespace Caliper.Core.Configuration;

// A8: string enum in config.json for hand-editability (see CaliperOptions.cs's
// ExecutionBackendKind remark for the full rationale). TurnStrategyKind is config-only
// (CaliperOptions.TurnStrategy / the legacy, unused AgentOptions.TurnStrategy) — never part of a
// persisted session payload or the model-facing wire protocol.
[JsonConverter(typeof(JsonStringEnumConverter<TurnStrategyKind>))]
public enum TurnStrategyKind
{
    Auto,
    Native,
    Constrained,
    SingleEnvelope,
    TwoPhase,
}
