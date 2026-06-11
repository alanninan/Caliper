// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Configuration;

public sealed class PersistenceOptions
{
    public string SqlitePath { get; set; } = "~/.caliper/caliper.db";
}
