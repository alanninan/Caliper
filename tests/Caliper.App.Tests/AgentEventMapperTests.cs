// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using System.Text.Json;
using Caliper.App.ViewModels;
using Caliper.Core.Agents;
using Caliper.Core.Events;
using Caliper.Core.Models;
using Microsoft.Extensions.Time.Testing;

namespace Caliper.App.Tests;

public sealed class AgentEventMapperTests
{
    [Fact]
    public void Map_streaming_and_final_message_uses_single_assistant_item()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var mapper = new AgentEventMapper(items);

        mapper.Map(new AssistantMessageDelta("Hello"));
        mapper.Map(new AssistantMessageDelta(" world"));
        mapper.Map(new AssistantMessage("Hello **world**"));

        var message = Assert.IsType<AssistantMessageViewModel>(Assert.Single(items));
        Assert.Equal("Hello **world**", message.Content);
        Assert.False(message.IsStreaming);
    }

    [Fact]
    public void FlushStreamingMessage_coalesces_pending_deltas()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var mapper = new AgentEventMapper(items);

        _ = mapper.Map(new AssistantMessageDelta("one "));
        _ = mapper.Map(new AssistantMessageDelta("two "));
        _ = mapper.Map(new AssistantMessageDelta("three"));

        var message = Assert.IsType<AssistantMessageViewModel>(Assert.Single(items));
        Assert.Empty(message.Content);
        Assert.True(mapper.FlushStreamingMessage());
        Assert.Equal("one two three", message.Content);
        Assert.False(mapper.FlushStreamingMessage());
    }

    // Crash hardening (TO_FIX.md item 2): FlushStreamingMessage(TimeSpan) throttles the tick-driven
    // push of streaming content into a bubble's Content, to cut the MarkdownTextBlock re-parse churn
    // behind the layout-cycle crash. These cases drive it directly with a FakeTimeProvider, matching
    // how ChatViewModel.FlushStreamingAsync calls it every StreamingFlushInterval tick.
    [Fact]
    public void FlushStreamingMessage_throttled_tick_without_boundary_does_not_push_content()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-16T09:30:00Z"));
        var mapper = new AgentEventMapper(items, timeProvider);
        var minInterval = TimeSpan.FromMilliseconds(250);

        mapper.Map(new AssistantMessageDelta("Hello"));
        // A bubble's very first flush always pushes (no prior push to throttle against), so the
        // first characters of a stream appear without an artificial initial delay.
        Assert.True(mapper.FlushStreamingMessage(minInterval));
        var message = Assert.IsType<AssistantMessageViewModel>(Assert.Single(items));
        Assert.Equal("Hello", message.Content);

        mapper.Map(new AssistantMessageDelta(" world"));
        timeProvider.Advance(TimeSpan.FromMilliseconds(80));
        Assert.False(mapper.FlushStreamingMessage(minInterval));
        Assert.Equal("Hello", message.Content);
    }

    [Fact]
    public void FlushStreamingMessage_throttled_tick_with_newline_pushes_immediately()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-16T09:30:00Z"));
        var mapper = new AgentEventMapper(items, timeProvider);
        var minInterval = TimeSpan.FromMilliseconds(250);

        mapper.Map(new AssistantMessageDelta("Hello"));
        Assert.True(mapper.FlushStreamingMessage(minInterval));
        var message = Assert.IsType<AssistantMessageViewModel>(Assert.Single(items));

        mapper.Map(new AssistantMessageDelta(" world\nmore"));
        timeProvider.Advance(TimeSpan.FromMilliseconds(80));
        Assert.True(mapper.FlushStreamingMessage(minInterval));
        Assert.Equal("Hello world\nmore", message.Content);
    }

    [Fact]
    public void FlushStreamingMessage_throttled_tick_with_code_fence_pushes_immediately()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-16T09:30:00Z"));
        var mapper = new AgentEventMapper(items, timeProvider);
        var minInterval = TimeSpan.FromMilliseconds(250);

        mapper.Map(new AssistantMessageDelta("Hello "));
        Assert.True(mapper.FlushStreamingMessage(minInterval));
        var message = Assert.IsType<AssistantMessageViewModel>(Assert.Single(items));

        mapper.Map(new AssistantMessageDelta("```csharp"));
        timeProvider.Advance(TimeSpan.FromMilliseconds(80));
        Assert.True(mapper.FlushStreamingMessage(minInterval));
        Assert.Equal("Hello ```csharp", message.Content);
    }

    [Fact]
    public void FlushStreamingMessage_throttled_tick_after_min_interval_pushes_content()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-16T09:30:00Z"));
        var mapper = new AgentEventMapper(items, timeProvider);
        var minInterval = TimeSpan.FromMilliseconds(250);

        mapper.Map(new AssistantMessageDelta("Hello"));
        Assert.True(mapper.FlushStreamingMessage(minInterval));
        var message = Assert.IsType<AssistantMessageViewModel>(Assert.Single(items));

        mapper.Map(new AssistantMessageDelta(" world"));
        timeProvider.Advance(TimeSpan.FromMilliseconds(260));
        Assert.True(mapper.FlushStreamingMessage(minInterval));
        Assert.Equal("Hello world", message.Content);
    }

    [Fact]
    public void FlushStreamingMessage_throttles_assistant_and_reasoning_bubbles_independently()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-16T09:30:00Z"));
        var mapper = new AgentEventMapper(items, timeProvider);
        var minInterval = TimeSpan.FromMilliseconds(250);

        mapper.Map(new Caliper.Core.Events.ReasoningDelta("thinking"));
        mapper.Map(new AssistantMessageDelta("Hello"));
        Assert.True(mapper.FlushStreamingMessage(minInterval));
        var reasoning = Assert.IsType<ReasoningViewModel>(items[0]);
        var assistant = Assert.IsType<AssistantMessageViewModel>(items[1]);
        Assert.Equal("thinking", reasoning.Content);
        Assert.Equal("Hello", assistant.Content);

        // Only the assistant chunk crosses a structural boundary; the reasoning bubble has its own
        // last-push state and stays withheld within the throttle window.
        mapper.Map(new Caliper.Core.Events.ReasoningDelta(" more"));
        mapper.Map(new AssistantMessageDelta("\nworld"));
        timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(mapper.FlushStreamingMessage(minInterval));
        Assert.Equal("thinking", reasoning.Content);
        Assert.Equal("Hello\nworld", assistant.Content);
    }

    [Fact]
    public void Map_turn_started_mid_stream_forces_pending_content_flush()
    {
        // FinalizeStreamingMessage (invoked here via TurnStarted, and also by RunCompleted/RunFailed/
        // ResetForRun) always uses the unconditional FlushStreamingMessage() overload — a turn/run
        // boundary must render the complete final content even if the last chunk hasn't crossed the
        // throttle window yet, or the tail of a streamed message would be silently dropped.
        ObservableCollection<ChatItemViewModel> items = [];
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-16T09:30:00Z"));
        var mapper = new AgentEventMapper(items, timeProvider);

        mapper.Map(new AssistantMessageDelta("Hello"));
        Assert.True(mapper.FlushStreamingMessage(TimeSpan.FromMilliseconds(250)));
        var message = Assert.IsType<AssistantMessageViewModel>(Assert.Single(items));

        mapper.Map(new AssistantMessageDelta(" world"));
        timeProvider.Advance(TimeSpan.FromMilliseconds(50));

        mapper.Map(new TurnStarted(2));
        Assert.Equal("Hello world", message.Content);
        Assert.False(message.IsStreaming);
    }

    [Fact]
    public void Map_cancelled_completion_records_terminal_status()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var mapper = new AgentEventMapper(items);

        _ = mapper.Map(new RunCompleted(CompletionReason.Cancelled));

        var reason = mapper.LastCompletionReason;
        Assert.True(reason.HasValue);
        Assert.Equal(CompletionReason.Cancelled, reason.GetValueOrDefault());
        Assert.Equal(
            ChatRunStatus.Cancelled,
            ChatRunStatusExtensions.FromCompletion(reason.GetValueOrDefault()));
    }

    [Fact]
    public void Map_tool_events_correlates_result_by_call_id()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var mapper = new AgentEventMapper(items);
        var arguments = JsonSerializer.SerializeToElement(new { path = "README.md" });

        mapper.Map(new ToolInvoked("call-1", "read_file", arguments));
        mapper.Map(new ToolSucceeded("call-1", "read_file", "contents"));

        var activity = Assert.IsType<ToolActivityViewModel>(Assert.Single(items));
        var tool = Assert.Single(activity.Calls);
        Assert.Equal("Succeeded", tool.Status);
        Assert.Equal("contents", tool.Output);
        Assert.False(activity.IsRunning);
        Assert.False(activity.HasFailure);
    }

    [Fact]
    public void Map_failed_tool_without_invocation_creates_failed_card()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var mapper = new AgentEventMapper(items);

        mapper.Map(new ToolFailed("call-1", "write_file", "Permission denied."));

        var activity = Assert.IsType<ToolActivityViewModel>(Assert.Single(items));
        var tool = Assert.Single(activity.Calls);
        Assert.Equal("Failed", tool.Status);
        Assert.True(tool.IsExpanded);
        Assert.True(activity.HasFailure);
        Assert.True(activity.IsExpanded);
    }

    [Fact]
    public void Map_denied_tool_shows_denied_status_matching_reload_path()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var mapper = new AgentEventMapper(items);
        var arguments = JsonSerializer.SerializeToElement(new { command = "rm -rf /" });
        var call = new ToolCall("call-1", "bash", arguments);

        // Live: the engine reports a denial as ToolFailed carrying ToolResult.Denied's output.
        mapper.Map(new ToolInvoked("call-1", "bash", arguments));
        mapper.Map(new ToolFailed("call-1", "bash", ToolResult.Denied.Output));
        var liveActivity = Assert.IsType<ToolActivityViewModel>(Assert.Single(items));
        var liveTool = Assert.Single(liveActivity.Calls);

        // Reload: the same call rebuilt from the persisted transcript.
        var reloaded = PersistedTranscriptFactory.Create(
            [ChatMessage.FromToolCall(call), ChatMessage.FromToolResult(call, ToolResult.Denied)]);
        var reloadedTool = Assert.Single(Assert.IsType<ToolActivityViewModel>(Assert.Single(reloaded)).Calls);

        Assert.Equal("Denied", liveTool.Status);
        Assert.Equal(reloadedTool.Status, liveTool.Status);
    }

    [Fact]
    public void Map_denied_tool_counts_as_failure_in_activity_summary()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var mapper = new AgentEventMapper(items);
        var arguments = JsonSerializer.SerializeToElement(new { command = "ls" });

        mapper.Map(new ToolInvoked("call-1", "bash", arguments));
        mapper.Map(new ToolFailed("call-1", "bash", ToolResult.Denied.Output));

        var activity = Assert.IsType<ToolActivityViewModel>(Assert.Single(items));
        Assert.True(activity.HasFailure);
        Assert.True(activity.IsExpanded);
        Assert.Contains("1 failed", activity.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_ordinary_failure_with_different_output_stays_failed()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var mapper = new AgentEventMapper(items);
        var arguments = JsonSerializer.SerializeToElement(new { command = "ls" });

        mapper.Map(new ToolInvoked("call-1", "bash", arguments));
        mapper.Map(new ToolFailed("call-1", "bash", "exit code 1"));

        var activity = Assert.IsType<ToolActivityViewModel>(Assert.Single(items));
        var tool = Assert.Single(activity.Calls);
        Assert.Equal("Failed", tool.Status);
    }

    [Fact]
    public void Map_file_change_builds_diff_details()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var mapper = new AgentEventMapper(items);
        var arguments = JsonSerializer.SerializeToElement(new { path = "README.md", old_str = "old", new_str = "new" });
        mapper.Map(new ToolInvoked("call-2", "edit_file", arguments));

        mapper.Map(new ToolSucceeded(
            "call-2",
            "edit_file",
            "Edited README.md",
            new FileChange("README.md", "old\n", "new\n")));

        var activity = Assert.IsType<ToolActivityViewModel>(Assert.Single(items));
        var tool = Assert.Single(activity.Calls);
        Assert.True(tool.HasDiff);
        Assert.NotEmpty(tool.Diff!.InlineRows);
        Assert.Contains(tool.Diff.InlineRows, line => line.Kind == DiffLineKind.Added);
        Assert.Contains(tool.Diff.InlineRows, line => line.Kind == DiffLineKind.Removed);
    }

    [Fact]
    public void Map_consecutive_tool_calls_within_one_turn_group_into_one_activity()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var mapper = new AgentEventMapper(items);
        var arguments = JsonSerializer.SerializeToElement(new { path = "README.md" });

        mapper.Map(new ToolInvoked("call-1", "read_file", arguments));
        mapper.Map(new ToolSucceeded("call-1", "read_file", "contents"));
        mapper.Map(new ToolInvoked("call-2", "grep", arguments));
        mapper.Map(new ToolSucceeded("call-2", "grep", "matches"));

        var activity = Assert.IsType<ToolActivityViewModel>(Assert.Single(items));
        Assert.Equal(2, activity.Calls.Count);
    }

    [Fact]
    public void Map_assistant_message_sets_timestamp_from_time_provider()
    {
        // U7: the live-created assistant message carries the mapper's TimeProvider timestamp, so the
        // transcript bubble can show it on hover; a reloaded (persisted) message has none instead
        // (see PersistedTranscriptFactoryTests).
        ObservableCollection<ChatItemViewModel> items = [];
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-16T09:30:00Z"));
        var mapper = new AgentEventMapper(items, timeProvider);

        mapper.Map(new AssistantMessageDelta("Hi"));
        mapper.Map(new AssistantMessage("Hi"));

        var message = Assert.IsType<AssistantMessageViewModel>(Assert.Single(items));
        Assert.Equal(timeProvider.GetUtcNow(), message.Timestamp);
    }

    [Fact]
    public void Map_tool_calls_across_turns_create_separate_activities()
    {
        ObservableCollection<ChatItemViewModel> items = [];
        var mapper = new AgentEventMapper(items);
        var arguments = JsonSerializer.SerializeToElement(new { path = "README.md" });

        mapper.Map(new TurnStarted(1));
        mapper.Map(new ToolInvoked("call-1", "read_file", arguments));
        mapper.Map(new ToolSucceeded("call-1", "read_file", "contents"));
        mapper.Map(new TurnStarted(2));
        mapper.Map(new ToolInvoked("call-2", "grep", arguments));
        mapper.Map(new ToolSucceeded("call-2", "grep", "matches"));

        Assert.Equal(2, items.OfType<ToolActivityViewModel>().Count());
    }
}
