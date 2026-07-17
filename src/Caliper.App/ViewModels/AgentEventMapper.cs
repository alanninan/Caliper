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

    // Crash hardening (TO_FIX.md item 2): per-bubble throttle state for FlushStreamingMessage.
    // Tracks how much of each streaming buffer has already reached the bound Content and when that
    // last happened, so a tick can decide "not yet" without losing track of what's pending. Reset
    // whenever a fresh bubble is created (EnsureAssistant/EnsureReasoning) — state from a prior
    // turn's bubble must never gate a new one's first push.
    private DateTimeOffset? _assistantPushedAt;
    private int _assistantPushedLength;
    private DateTimeOffset? _reasoningPushedAt;
    private int _reasoningPushedLength;

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
            case SubagentStarted started:
                // Keep-it-simple status card (roadmap §3.1): the child session is an ordinary
                // session so it's already inspectable from the sessions pane (behind the "Subagent
                // runs" toggle); live streaming of the child's own events into this transcript is
                // explicitly deferred (roadmap §8).
                items.Add(new RunStatusViewModel("Subagent started", started.Title));
                return true;
            case SubagentCompleted completed:
                var reasonText = completed.Reason?.ToString() ?? "Error";
                items.Add(new RunStatusViewModel(
                    "Subagent finished",
                    reasonText,
                    isError: completed.Reason is not CompletionReason.Completed));
                return true;
            case ContextCompacted compacted:
                items.Add(new CompactionMarkerViewModel(
                    "Conversation compacted",
                    detail: $"{compacted.BeforeTokens:N0} → {compacted.AfterTokens:N0} tokens"));
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

    // Unconditional flush (used by FinalizeStreamingMessage's turn/run-boundary safety net, and by
    // ChatViewModel's post-run cleanup): every bubble's Content is brought fully up to date with its
    // streaming buffer regardless of throttle state, since a turn/run boundary must never drop the
    // tail of a streamed message that hasn't yet crossed the throttle window.
    public bool FlushStreamingMessage() => FlushStreamingMessage(TimeSpan.Zero);

    // Called every ChatViewModel.FlushStreamingAsync tick with minPushInterval set to
    // MinStreamingRenderInterval: a bubble's Content is only re-pushed once minPushInterval has
    // elapsed since its last push, OR the text appended since that push crosses a structural
    // boundary (newline or a ``` fence, so paragraph breaks and code fences still render promptly).
    // minPushInterval of TimeSpan.Zero (the parameterless overload above) always pushes.
    public bool FlushStreamingMessage(TimeSpan minPushInterval)
    {
        var changed = false;

        if (_assistant is not null)
        {
            var content = _streamingContent.ToString();
            if (!string.Equals(_assistant.Content, content, StringComparison.Ordinal) &&
                ShouldPush(content, _assistantPushedLength, _assistantPushedAt, minPushInterval))
            {
                _assistant.Content = content;
                _assistantPushedAt = _timeProvider.GetUtcNow();
                _assistantPushedLength = content.Length;
                changed = true;
            }
        }

        if (_reasoning is not null)
        {
            var content = _streamingReasoning.ToString();
            if (!string.Equals(_reasoning.Content, content, StringComparison.Ordinal) &&
                ShouldPush(content, _reasoningPushedLength, _reasoningPushedAt, minPushInterval))
            {
                _reasoning.Content = content;
                _reasoningPushedAt = _timeProvider.GetUtcNow();
                _reasoningPushedLength = content.Length;
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

    // A bubble with no prior push (pushedAt is null — freshly created by Ensure*) always pushes
    // immediately, so the first characters of a stream appear without an artificial initial delay.
    // After that, throttle to minPushInterval unless the newly appended text crosses a structural
    // boundary.
    private bool ShouldPush(string content, int pushedLength, DateTimeOffset? pushedAt, TimeSpan minPushInterval)
    {
        if (minPushInterval <= TimeSpan.Zero || pushedAt is null)
            return true;

        if (_timeProvider.GetUtcNow() - pushedAt.Value >= minPushInterval)
            return true;

        ReadOnlySpan<char> appended = pushedLength < content.Length
            ? content.AsSpan(pushedLength)
            : ReadOnlySpan<char>.Empty;
        return appended.Contains('\n') || appended.Contains("```", StringComparison.Ordinal);
    }

    private AssistantMessageViewModel EnsureAssistant()
    {
        if (_assistant is not null)
            return _assistant;

        _assistant = new AssistantMessageViewModel { Timestamp = _timeProvider.GetUtcNow() };
        _assistantPushedAt = null;
        _assistantPushedLength = 0;
        items.Add(_assistant);
        return _assistant;
    }

    private ReasoningViewModel EnsureReasoning()
    {
        if (_reasoning is not null)
            return _reasoning;

        _reasoning = new ReasoningViewModel { StartedAt = _timeProvider.GetUtcNow() };
        _reasoningPushedAt = null;
        _reasoningPushedLength = 0;
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

        // Same denial detection as PersistedTranscriptFactory: a denied call must read "Denied"
        // live, exactly matching what the reload path produces from the stored transcript.
        var denied = !succeeded && ToolCallStatus.IsDenial(output);
        tool.Status = succeeded
            ? ToolCallStatus.Succeeded
            : denied
                ? ToolCallStatus.Denied
                : ToolCallStatus.Failed;
        tool.Output = output;
        tool.IsExpanded = !succeeded;
        tool.SetFileChange(fileChange);
    }
}
