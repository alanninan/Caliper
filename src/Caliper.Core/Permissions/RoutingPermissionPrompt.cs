// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Events;

namespace Caliper.Core.Permissions;

/// <summary>
/// Routes each permission request by its run's <c>RunSpec.Unattended</c> flag
/// (<see cref="PermissionRequest.Unattended"/>): unattended requests go to the deny+report
/// <see cref="UnattendedPermissionPrompt"/>, everything else to the host's interactive prompt.
/// </summary>
/// <remarks>
/// Registered as the console's <c>IPermissionPrompt</c> in interactive REPL mode (roadmap §3.2b),
/// where <c>/schedule run</c> triggers a job through the identical unattended path a
/// <c>--serve</c> scheduler tick would use: the run itself renders live in the terminal, but its
/// permission prompts are denied and recorded, never shown to the human. Headless modes don't
/// need the split — <c>--serve</c> and <c>--unattended</c> register
/// <see cref="UnattendedPermissionPrompt"/> outright — and the App keeps its own
/// <c>ApprovalService</c> untouched (it never builds a spec with <c>Unattended = true</c>).
/// <see cref="PermissionGate"/> is unchanged: routing happens entirely at the prompt seam.
/// </remarks>
public sealed class RoutingPermissionPrompt(
    IPermissionPrompt interactive,
    UnattendedPermissionPrompt unattended) : IPermissionPrompt
{
    public Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken ct) =>
        request.Unattended
            ? unattended.AskAsync(request, ct)
            : interactive.AskAsync(request, ct);
}
