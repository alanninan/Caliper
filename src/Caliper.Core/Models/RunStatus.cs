// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Models;

/// <summary>
/// Coarse lifecycle status of a tracked run (roadmap §3.4 durable execution). <see cref="Running"/>
/// is written at start and never observed as "true" on process restart — see the startup sweep on
/// <c>SqliteRunStore</c>, which flips any row still <see cref="Running"/> to <see cref="Interrupted"/>
/// the moment the store initializes (single local writer: if it says running and we're just
/// starting, it isn't). The other three terminal values are written by
/// <c>ConversationOrchestrator</c>'s <c>MapCompletionStatus</c> from the run's
/// <see cref="Caliper.Core.Events.CompletionReason"/> (or an unhandled exception).
/// </summary>
public enum RunStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
    Interrupted,
}
