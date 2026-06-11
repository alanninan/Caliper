// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Caliper.Core.Abstractions;

public interface IToolRegistry
{
    IReadOnlyList<ITool> Enabled { get; }
    ITool? Find(string name);
    IReadOnlyList<AIFunction> AsAIFunctions();
    JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu);
    string BuildToolMenu();
}
