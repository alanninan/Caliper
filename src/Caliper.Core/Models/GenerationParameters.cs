// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Models;

public sealed record GenerationParameters(
    double Temperature = 0.0,
    int?   Seed = null,
    int    NumCtx = 32768,
    int    MaxOutputTokens = 1024,
    IReadOnlyList<string>? Stop = null);
