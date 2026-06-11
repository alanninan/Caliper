// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Microsoft.Extensions.AI;

namespace Caliper.Core.Abstractions;

public interface IChatClientProvider
{
    IChatClient GetClient(string modelSlug);
}
