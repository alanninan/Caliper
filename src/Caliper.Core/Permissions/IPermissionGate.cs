// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Events;

namespace Caliper.Core.Permissions;

public interface IPermissionGate
{
    Task<PermissionDecision> EvaluateAsync(PermissionRequest request, CancellationToken ct);

    /// <summary>
    /// Clears "allow for session" approvals. Pass a <paramref name="sessionId"/> to clear only that
    /// session's grants (e.g. when a session is deleted); pass null to clear every session's grants
    /// (e.g. a single-session host starting a fresh conversation). Approvals are scoped per session,
    /// so switching between sessions never needs a reset.
    /// </summary>
    void ResetSessionApprovals(string? sessionId = null) { }
}

public interface IPermissionPrompt
{
    Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken ct);
}
