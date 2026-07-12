// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Events;

namespace Caliper.App.ViewModels;

public enum ChatRunStatus
{
    Ready,
    Running,
    Stopping,
    Cancelled,
    StepLimit,
    LoopDetected,
    Failed,
}

public static class ChatRunStatusExtensions
{
    public static ChatRunStatus FromCompletion(CompletionReason reason) =>
        reason switch
        {
            CompletionReason.Completed => ChatRunStatus.Ready,
            CompletionReason.Cancelled => ChatRunStatus.Cancelled,
            CompletionReason.StepLimit => ChatRunStatus.StepLimit,
            CompletionReason.LoopDetected => ChatRunStatus.LoopDetected,
            // An unrecognized reason (e.g. a new value added to Core) must degrade gracefully
            // instead of crashing the send loop.
            _ => ChatRunStatus.Failed,
        };

    public static string ToDisplayText(this ChatRunStatus status) =>
        status switch
        {
            ChatRunStatus.Ready => "Ready",
            ChatRunStatus.Running => "Running",
            ChatRunStatus.Stopping => "Stopping",
            ChatRunStatus.Cancelled => "Cancelled",
            ChatRunStatus.StepLimit => "Step limit reached",
            ChatRunStatus.LoopDetected => "Loop detected",
            ChatRunStatus.Failed => "Failed",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
}
