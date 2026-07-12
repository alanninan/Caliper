// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Models;
using Caliper.Core.Protocol;
using Caliper.Core.Tools;
using Microsoft.Extensions.AI;

namespace Caliper.Evals;

// Intercepting tool that returns a scripted string response.
internal class MockTool(
    string name,
    string description,
    JsonElement parameterSchema,
    string response,
    SideEffect sideEffect) : ITool
{
    public string Name              => name;
    public string Description       => description;
    public JsonElement ParameterSchema => parameterSchema;
    public SideEffect SideEffect => sideEffect;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct) =>
        Task.FromResult(new ToolResult(true, response));
}

internal sealed class MockMcpTool(
    string name,
    string description,
    JsonElement parameterSchema,
    string response,
    SideEffect sideEffect) : MockTool(name, description, parameterSchema, response, sideEffect), IMcpTool;

// IToolRegistry backed entirely by mock tools.
internal sealed class MockToolRegistry(IReadOnlyDictionary<string, (string Description, JsonElement Schema, string Response, SideEffect SideEffect, bool IsMcp)> specs) : IToolRegistry
{
    private static readonly JsonElement s_searchSchema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["query"],"properties":{"query":{"type":"string","maxLength":256}}}"""
    ).RootElement.Clone();

    private static readonly JsonElement s_fetchUrlSchema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["url"],"properties":{"url":{"type":"string","maxLength":2048}}}"""
    ).RootElement.Clone();

    private static readonly JsonElement s_genericSchema = JsonDocument.Parse(
        """{"type":"object"}"""
    ).RootElement.Clone();

    private readonly IReadOnlyList<ITool> _tools =
        specs.Select(kv => kv.Value.IsMcp
                ? (ITool)new MockMcpTool(kv.Key, kv.Value.Description, kv.Value.Schema, kv.Value.Response, kv.Value.SideEffect)
                : new MockTool(kv.Key, kv.Value.Description, kv.Value.Schema, kv.Value.Response, kv.Value.SideEffect))
            .ToList();

    public IReadOnlyList<ITool> Enabled => _tools;

    public IReadOnlyList<ITool> All => _tools;

    public ITool? Find(string name) => _tools.FirstOrDefault(t => t.Name == name);

    public IReadOnlyList<AIFunction> AsAIFunctions() => [];

    public JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu) =>
        ProtocolBuilder.BuildSchema(_tools.Select(t => (t.Name, t.ParameterSchema)).ToList(), skillMenu);

    public string BuildToolMenu() =>
        string.Join("\n", _tools.Select(t => $"- {t.Name}: {t.Description}"));

    // Factory that picks the right schema for well-known tools.
    internal static MockToolRegistry FromSpecs(IReadOnlyList<MockToolSpec> specs) =>
        new(specs.ToDictionary(
            spec => spec.Name,
            spec => (
                spec.Description,
                spec.Schema,
                spec.Response,
                spec.SideEffect,
                spec.IsMcp
            ),
            StringComparer.Ordinal));

    internal static MockToolRegistry FromMockResponses(IReadOnlyDictionary<string, string> responses) =>
        new(responses.ToDictionary(
            kv => kv.Key,
            kv => (
                Description: $"Mock tool: {kv.Key}",
                Schema: kv.Key switch
                {
                    "search" => s_searchSchema,
                    "fetch_url" => s_fetchUrlSchema,
                    _ => s_genericSchema,
                },
                Response: kv.Value,
                SideEffect: SideEffect.Network,
                IsMcp: false
            )));

    internal static JsonElement SchemaFor(string toolName) =>
        toolName switch
        {
            "search" => s_searchSchema,
            "fetch_url" => s_fetchUrlSchema,
            "bash" or "powershell" => JsonDocument.Parse(
                """{"type":"object","additionalProperties":false,"required":["command"],"properties":{"command":{"type":"string"},"cwd":{"type":"string"}}}""").RootElement.Clone(),
            "write_file" => JsonDocument.Parse(
                """{"type":"object","additionalProperties":false,"required":["path","content"],"properties":{"path":{"type":"string"},"content":{"type":"string"}}}""").RootElement.Clone(),
            _ => s_genericSchema,
        };
}

// Registry with no tools — used for respond-only eval cases.
internal sealed class EmptyEvalToolRegistry : IToolRegistry
{
    internal static readonly EmptyEvalToolRegistry Instance = new();
    public IReadOnlyList<ITool> Enabled => [];
    public IReadOnlyList<ITool> All => [];
    public ITool? Find(string name) => null;
    public IReadOnlyList<AIFunction> AsAIFunctions() => [];
    public JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu) =>
        ProtocolBuilder.BuildSchema([], skillMenu);
    public string BuildToolMenu() => string.Empty;
}
