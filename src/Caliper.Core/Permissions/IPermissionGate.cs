// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Events;

namespace Caliper.Core.Permissions;

public interface IPermissionGate
{
    Task<PermissionDecision> EvaluateAsync(PermissionRequest request, CancellationToken ct);
}

public interface IPermissionPrompt
{
    Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken ct);
}
