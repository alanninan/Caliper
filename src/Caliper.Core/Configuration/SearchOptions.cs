// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Configuration;

public sealed class SearchOptions
{
    public string Backend { get; set; } = "Stub";
    public string? ApiKey { get; set; }
    public string SearchDepth { get; set; } = "basic";
    public int MaxResults { get; set; } = 5;
    public string Topic { get; set; } = "general";
}
