// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Models;

public sealed record ToolResult(bool Success, string Output)
{
    public static ToolResult Denied { get; } = new(false, "Denied by user/policy.");
}
