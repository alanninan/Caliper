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
[JsonSerializable(typeof(OpenAIModelsResponse))]
[JsonSerializable(typeof(OpenAIModel))]
[JsonSerializable(typeof(CodexDeviceCodeRequest))]
[JsonSerializable(typeof(CodexDeviceCodeResponse))]
[JsonSerializable(typeof(CodexDeviceTokenRequest))]
[JsonSerializable(typeof(CodexDeviceTokenResponse))]
[JsonSerializable(typeof(CodexRefreshRequest))]
[JsonSerializable(typeof(CodexTokenResponse))]
[JsonSerializable(typeof(CodexModelsResponse))]
[JsonSerializable(typeof(CodexModel))]
[JsonSerializable(typeof(CodexReasoningLevel))]
[JsonSerializable(typeof(Dictionary<string, string>))]
// Config option types — registered so IConfigWriter can serialize typed sections back into
// config.json without reflection (JsonSerializerIsReflectionEnabledByDefault=false).
[JsonSerializable(typeof(CaliperOptions))]
[JsonSerializable(typeof(ReasoningOptions))]
[JsonSerializable(typeof(ContextOptions))]
[JsonSerializable(typeof(MemoryOptions))]
[JsonSerializable(typeof(ProvidersOptions))]
[JsonSerializable(typeof(OpenRouterOptions))]
[JsonSerializable(typeof(GeminiOptions))]
[JsonSerializable(typeof(OpenAIOptions))]
[JsonSerializable(typeof(OpenAICodexOptions))]
[JsonSerializable(typeof(PermissionsOptions))]
[JsonSerializable(typeof(McpOptions))]
[JsonSerializable(typeof(McpServerOptions))]
[JsonSerializable(typeof(SearchOptions))]
[JsonSerializable(typeof(PersistenceOptions))]
[JsonSerializable(typeof(SubagentsOptions))]
[JsonSerializable(typeof(SubagentProfileOptions))]
[JsonSerializable(typeof(SchedulerOptions))]
[JsonSerializable(typeof(ScheduleOptions))]
[JsonSerializable(typeof(List<ScheduleOptions>))]
[JsonSerializable(typeof(ExecutionOptions))]
internal sealed partial class CaliperJsonContext : JsonSerializerContext { }
