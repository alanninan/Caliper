// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text;
using Caliper.Core.Agents;
using Caliper.Core.Events;
using Spectre.Console;
using EventReasoningDelta = Caliper.Core.Events.ReasoningDelta;

namespace Caliper.Console.Rendering;

public sealed class EventRenderer(
    StatusFooter footer,
    bool printOnly = false)
{
    private readonly StringBuilder _assistantPreview = new();
    private bool _inThought;
    private UsageInfo? _lastUsage;
    private bool _compacted;

    public void Render(AgentEvent evt)
    {
        if (printOnly)
        {
            if (evt is AssistantMessage message)
                System.Console.WriteLine(message.Content);
            return;
        }

        switch (evt)
        {
            case TurnStarted { Step: > 1 } ts:
                AnsiConsole.MarkupLine($"[dim]-- step {ts.Step} --[/]");
                break;

            case EventReasoningDelta(var text):
                if (!_inThought)
                {
                    AnsiConsole.Markup("[dim italic]Thinking: [/]");
                    _inThought = true;
                }
                AnsiConsole.Markup($"[dim italic]{Markup.Escape(text)}[/]");
                break;

            case ReasoningCompleted:
                if (_inThought)
                {
                    System.Console.WriteLine();
                    _inThought = false;
                }
                break;

            case AssistantMessageDelta(var text):
                _assistantPreview.Append(text);
                System.Console.Write(text);
                break;

            case AssistantMessage(var content):
                if (_assistantPreview.Length > 0)
                {
                    // Already streamed live; just finish the line rather than re-rendering the
                    // whole message and printing it a second time.
                    System.Console.WriteLine();
                    _assistantPreview.Clear();
                }
                else
                {
                    AnsiConsole.Write(MarkdownRenderer.Render(content));
                    System.Console.WriteLine();
                }
                break;

            case ToolInvoked invoked:
                AnsiConsole.Write(ToolPanelRenderer.ToolInvoked(invoked));
                break;

            case ToolSucceeded succeeded:
                AnsiConsole.Write(ToolPanelRenderer.ToolSucceeded(succeeded));
                break;

            case ToolFailed failed:
                AnsiConsole.Write(ToolPanelRenderer.ToolFailed(failed));
                break;

            case PermissionRequested(var request):
                AnsiConsole.Write(ToolPanelRenderer.PermissionRequested(request));
                break;

            case PermissionResolved resolved:
                AnsiConsole.Write(ToolPanelRenderer.PermissionResolved(resolved));
                break;

            case UsageReported(_, _, var cumulativePrompt, var cumulativeCompletion):
                _lastUsage = new UsageInfo(cumulativePrompt, cumulativeCompletion, cumulativePrompt + cumulativeCompletion);
                break;

            case SkillLoaded(var skill):
                AnsiConsole.MarkupLine($"[blue]Skill loaded:[/] {Markup.Escape(skill)}");
                break;

            case ContextCompacted(var before, var after):
                _compacted = true;
                AnsiConsole.MarkupLine($"[dim]context compacted {before}->{after}[/]");
                break;

            case McpServerConnected(var server, var toolCount):
                AnsiConsole.MarkupLine($"[green]MCP connected:[/] {Markup.Escape(server)} [dim]({toolCount} tools)[/]");
                break;

            case McpServerFailed(var server, var error):
                AnsiConsole.MarkupLine($"[red]MCP failed:[/] {Markup.Escape(server)} [dim]{Markup.Escape(error)}[/]");
                break;

            case RunCompleted completed:
                if (completed.Reason != CompletionReason.Completed)
                    AnsiConsole.MarkupLine($"[yellow]Run ended: {completed.Reason}[/]");
                footer.Write(_lastUsage, _compacted);
                _compacted = false;
                break;

            case RunFailed(var error):
                AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(error)}[/]");
                footer.Write(_lastUsage, _compacted);
                _compacted = false;
                break;
        }
    }
}
