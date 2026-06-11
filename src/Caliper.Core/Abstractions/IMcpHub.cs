// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Abstractions;

public interface IMcpHub
{
    Task ConnectAllAsync(CancellationToken ct);
    IReadOnlyList<ITool> Tools { get; }
    IReadOnlyList<McpServerStatus> Status { get; }
    Task DisposeAllAsync();
}

public sealed record McpServerStatus(string Name, bool Connected, int ToolCount, string? Error);

public interface IMcpTool : ITool;
