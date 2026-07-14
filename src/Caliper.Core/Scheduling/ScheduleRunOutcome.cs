// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Events;

namespace Caliper.Core.Scheduling;

/// <summary>
/// The recorded result of one scheduled-job occurrence (roadmap §3.2b). Kept in memory per job by
/// <see cref="ScheduleJobRunner"/> for <c>/schedule list</c>; the full transcript lives in the
/// session store like any other session. <paramref name="Skipped"/> marks an occurrence that was
/// skipped because the previous one was still running (overlap policy: skip, never queue).
/// </summary>
public sealed record ScheduleRunOutcome(
    string JobName,
    DateTimeOffset CompletedAt,
    CompletionReason? Reason,
    string? Error,
    int DenialCount,
    bool Skipped);
