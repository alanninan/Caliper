// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using System.Text;
using Caliper.Core.Events;

namespace Caliper.App.ViewModels;

public sealed class AgentEventMapper(ObservableCollection<ChatItemViewModel> items, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private AssistantMessageViewModel? _assistant;
    private readonly StringBuilder _streamingContent = new();
    private ReasoningViewModel? _reasoning;
    private readonly StringBuilder _streamingReasoning = new();
    private ToolActivityViewModel? _activity;

    // Cumulative across the whole run (all model calls so far), not just the last call.
    public int? PromptTokens { get; private set; }
    public int? CompletionTokens { get; private set; }
    public CompletionReason? LastCompletionReason { get; private set; }
    public string? Failure { get; private set; }

    public void ResetForRun()
    {
        FinalizeStreamingMessage();
        _activity = null;
        PromptTokens = null;
        CompletionTokens = null;
        LastCompletionReason = null;
        Failure = null;
    }

    public bool Map(AgentEvent evt)
    {
        switch (evt)
        {
            case TurnStarted:
                var turnChanged = FinalizeStreamingMessage();
                _activity = null;
                return turnChanged;
            case ReasoningDelta delta:
                _ = EnsureReasoning();
                _streamingReasoning.Append(delta.Text);
                return false;
            case ReasoningCompleted completed:
                var reasoning = EnsureReasoning();
                reasoning.Content = completed.Text;
                reasoning.ElapsedSeconds = (int)(_timeProvider.GetUtcNow() - reasoning.StartedAt).TotalSeconds;
                reasoning.IsStreaming = false;
                _reasoning = null;
                _streamingReasoning.Clear();
                return true;
            case AssistantMessageDelta delta:
                _ = EnsureAssistant();
                _streamingContent.Append(delta.Text);
                return false;
            case AssistantMessage message:
                var assistant = EnsureAssistant();
                assistant.Content = message.Content;
                assistant.IsStreaming = false;
                _assistant = null;
                _streamingContent.Clear();
                return true;
            case ToolInvoked invoked:
                _ = FinalizeStreamingMessage();
                EnsureActivity().Add(new ToolCallViewModel(
                    invoked.CallId,
                    invoked.Tool,
                    invoked.Arguments.GetRawText()));
                return true;
            case ToolSucceeded succeeded:
                CompleteTool(
                    succeeded.CallId,
                    succeeded.Tool,
                    succeeded.Output,
                    succeeded: true,
                    succeeded.FileChange);
                return true;
            case ToolFailed failed:
                CompleteTool(failed.CallId, failed.Tool, failed.Error, succeeded: false, fileChange: null);
                return true;
            case UsageReported usage:
                PromptTokens = usage.CumulativePrompt;
                CompletionTokens = usage.CumulativeCompletion;
                return false;
            case SkillLoaded loaded:
                items.Add(new RunStatusViewModel("Skill loaded", loaded.Skill));
                return true;
            case McpServerConnected connected:
                items.Add(new RunStatusViewModel(
                    "MCP connected",
                    $"{connected.Name} ({connected.ToolCount} tools)"));
                return true;
            case McpServerFailed failed:
                items.Add(new RunStatusViewModel("MCP connection failed", $"{failed.Name}: {failed.Error}", isError: true));
                return true;
            case ContextCompacted compacted:
                items.Add(new RunStatusViewModel(
                    "Context compacted",
                    $"{compacted.BeforeTokens} to {compacted.AfterTokens} tokens"));
                return true;
            case RunCompleted completed:
                LastCompletionReason = completed.Reason;
                var transcriptChanged = FinalizeStreamingMessage();
                if (completed.Reason != CompletionReason.Completed)
                {
                    items.Add(new RunStatusViewModel("Run ended", completed.Reason.ToString()));
                    return true;
                }
                return transcriptChanged;
            case RunFailed failed:
                Failure = failed.Error;
                _ = FinalizeStreamingMessage();
                items.Add(new RunStatusViewModel("Run failed", failed.Error, isError: true));
                return true;
            default:
                return false;
        }
    }

    public bool FlushStreamingMessage()
    {
        var changed = false;

        if (_assistant is not null)
        {
            var content = _streamingContent.ToString();
            if (!string.Equals(_assistant.Content, content, StringComparison.Ordinal))
            {
                _assistant.Content = content;
                changed = true;
            }
        }

        if (_reasoning is not null)
        {
            var content = _streamingReasoning.ToString();
            if (!string.Equals(_reasoning.Content, content, StringComparison.Ordinal))
            {
                _reasoning.Content = content;
                changed = true;
            }

            var elapsed = (int)(_timeProvider.GetUtcNow() - _reasoning.StartedAt).TotalSeconds;
            if (_reasoning.ElapsedSeconds != elapsed)
            {
                _reasoning.ElapsedSeconds = elapsed;
                changed = true;
            }
        }

        return changed;
    }

    private AssistantMessageViewModel EnsureAssistant()
    {
        if (_assistant is not null)
            return _assistant;

        _assistant = new AssistantMessageViewModel();
        items.Add(_assistant);
        return _assistant;
    }

    private ReasoningViewModel EnsureReasoning()
    {
        if (_reasoning is not null)
            return _reasoning;

        _reasoning = new ReasoningViewModel { StartedAt = _timeProvider.GetUtcNow() };
        items.Add(_reasoning);
        return _reasoning;
    }

    private ToolActivityViewModel EnsureActivity()
    {
        if (_activity is not null)
            return _activity;

        _activity = new ToolActivityViewModel();
        items.Add(_activity);
        return _activity;
    }

    private bool FinalizeStreamingMessage()
    {
        var changed = FlushStreamingMessage();

        if (_assistant is not null)
        {
            _assistant.IsStreaming = false;
            _assistant = null;
            _streamingContent.Clear();
            changed = true;
        }

        if (_reasoning is not null)
        {
            _reasoning.IsStreaming = false;
            _reasoning = null;
            _streamingReasoning.Clear();
            changed = true;
        }

        return changed;
    }

    private void CompleteTool(
        string callId,
        string toolName,
        string output,
        bool succeeded,
        Caliper.Core.Models.FileChange? fileChange)
    {
        var tool = items.OfType<ToolActivityViewModel>()
            .SelectMany(activity => activity.Calls)
            .LastOrDefault(item => item.CallId == callId);
        if (tool is null)
        {
            tool = new ToolCallViewModel(callId, toolName, string.Empty);
            EnsureActivity().Add(tool);
        }

        tool.Status = succeeded ? "Succeeded" : "Failed";
        tool.Output = output;
        tool.IsExpanded = !succeeded;
        tool.SetFileChange(fileChange);
    }
}
