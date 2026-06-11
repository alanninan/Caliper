// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Microsoft.Extensions.Logging;

namespace Caliper.Core.Tools;

/// <summary>Ambient services injected into every tool invocation.</summary>
public sealed class ToolContext(
    IHttpClientFactory httpClientFactory,
    ILogger logger,
    string skillsRootPath,
    string workingRoot,
    bool allowOutsideWorkingRoot,
    CancellationToken cancellationToken)
{
    public IHttpClientFactory HttpClientFactory { get; } = httpClientFactory;
    public ILogger Logger { get; } = logger;
    public string SkillsRootPath { get; } = skillsRootPath;
    public string WorkingRoot { get; } = workingRoot;
    public bool AllowOutsideWorkingRoot { get; } = allowOutsideWorkingRoot;
    public CancellationToken CancellationToken { get; } = cancellationToken;
}
