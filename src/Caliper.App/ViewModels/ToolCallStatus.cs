// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;

namespace Caliper.App.ViewModels;

/// <summary>
/// Shared tool-call status strings and permission-denial detection so the live event path
/// (<see cref="AgentEventMapper"/>) and the reload path (<see cref="PersistedTranscriptFactory"/>)
/// always produce the same transcript for the same tool outcome.
/// </summary>
internal static class ToolCallStatus
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Denied = "Denied";
    public const string Completed = "Completed";

    /// <summary>
    /// Whether a failed tool outcome is a permission denial. The engine reports a denial as a
    /// failed result whose output is exactly <see cref="ToolResult.Denied"/>'s output (see
    /// <c>AgentRunner</c>), so callers combine this with their own success check.
    /// </summary>
    public static bool IsDenial(string output) =>
        string.Equals(output, ToolResult.Denied.Output, StringComparison.Ordinal);
}
