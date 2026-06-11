// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;

namespace Caliper.Core.Abstractions;

public interface ISkillSelector
{
    Task<IReadOnlyList<string>> SelectAsync(
        string userMessage,
        IReadOnlyList<SkillMetadata> all,
        int max,
        CancellationToken ct);
}
