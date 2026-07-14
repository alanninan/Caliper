// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json.Serialization;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Caliper.Core.Tools;

namespace Caliper.Core.Protocol;

// Metadata mode is required for polymorphic serialization (fast-path doesn't support it).
// AgentTurn deserialization is handled entirely by AgentTurnConverter — no special
// polymorphic mode needed. Metadata mode kept for Ollama DTOs; fast-path suffices
// for the simple record types.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AgentTurn))]
[JsonSerializable(typeof(RespondTurn))]
[JsonSerializable(typeof(CallToolTurn))]
[JsonSerializable(typeof(LoadSkillTurn))]
[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaChatChunk))]
[JsonSerializable(typeof(TavilySearchRequest))]
[JsonSerializable(typeof(TavilySearchResponse))]
[JsonSerializable(typeof(ToolCallPayload))]
[JsonSerializable(typeof(ToolResultPayload))]
[JsonSerializable(typeof(FileChange))]
[JsonSerializable(typeof(LoadSkillArguments))]
[JsonSerializable(typeof(OpenRouterModelsResponse))]
[JsonSerializable(typeof(OpenRouterModel))]
// Config option types — registered so IConfigWriter can serialize typed sections back into
// config.json without reflection (JsonSerializerIsReflectionEnabledByDefault=false).
[JsonSerializable(typeof(CaliperOptions))]
[JsonSerializable(typeof(ReasoningOptions))]
[JsonSerializable(typeof(ContextOptions))]
[JsonSerializable(typeof(MemoryOptions))]
[JsonSerializable(typeof(ProvidersOptions))]
[JsonSerializable(typeof(OpenRouterOptions))]
[JsonSerializable(typeof(GeminiOptions))]
[JsonSerializable(typeof(PermissionsOptions))]
[JsonSerializable(typeof(McpOptions))]
[JsonSerializable(typeof(McpServerOptions))]
[JsonSerializable(typeof(SearchOptions))]
[JsonSerializable(typeof(PersistenceOptions))]
[JsonSerializable(typeof(SubagentsOptions))]
[JsonSerializable(typeof(SubagentProfileOptions))]
internal sealed partial class CaliperJsonContext : JsonSerializerContext { }
