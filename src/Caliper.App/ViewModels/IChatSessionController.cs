// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;

namespace Caliper.App.ViewModels;

public interface IChatSessionController
{
    string? CurrentSessionId { get; }
    string? RunningSessionId { get; }
    event EventHandler? RunActivityChanged;
    event EventHandler<SessionCreatedEventArgs>? SessionCreated;
    event EventHandler<SessionRenamedEventArgs>? SessionRenamed;
    Task SelectSessionAsync(string sessionId, CancellationToken ct);
    bool CanDeleteSession(string sessionId);
    void RemoveSession(string sessionId);
    void ClearSessionSelection();
}

public sealed class SessionCreatedEventArgs(SessionSummary summary) : EventArgs
{
    public SessionSummary Summary { get; } = summary;
}

public sealed class SessionRenamedEventArgs(string sessionId, string title) : EventArgs
{
    public string SessionId { get; } = sessionId;
    public string Title { get; } = title;
}
