// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Configuration;
using Spectre.Console;

namespace Caliper.Console.Rendering;

public sealed class StatusFooter(IRuntimeSettings settings, IMcpHub mcpHub)
{
    public string Format(UsageInfo? usage, bool compacted)
    {
        var caliper = settings.Caliper;
        var permissions = settings.Permissions;
        var connected = mcpHub.Status.Count(status => status.Connected);
        var usageText = usage is null
            ? "usage -/-"
            : $"usage {usage.PromptTokens?.ToString() ?? "-"}/{usage.CompletionTokens?.ToString() ?? "-"}";
        var compactedText = compacted ? " · compacted" : string.Empty;
        return $"{caliper.Model} · {permissions.Mode} · {usageText} · MCP {connected}{compactedText}";
    }

    public void Write(UsageInfo? usage, bool compacted) =>
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(Format(usage, compacted))}[/]");
}
