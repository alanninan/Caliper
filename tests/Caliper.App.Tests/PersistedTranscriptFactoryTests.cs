// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.App.ViewModels;
using Caliper.Core.Agents;
using Caliper.Core.Models;

namespace Caliper.App.Tests;

public sealed class PersistedTranscriptFactoryTests
{
    [Fact]
    public void Create_rebuilds_text_tool_and_summary_items()
    {
        var call = new ToolCall(
            "call-1",
            "read_file",
            JsonSerializer.SerializeToElement(new { path = "README.md" }));
        ChatMessage[] messages =
        [
            ChatMessage.Text(ChatRole.User, "Read it"),
            ChatMessage.FromToolCall(call),
            ChatMessage.FromToolResult(call, new ToolResult(true, "contents")),
            ChatMessage.Text(ChatRole.Assistant, "Done"),
            new ChatMessage(ChatRole.System, MessageKind.Summary, "Earlier context"),
        ];

        var transcript = PersistedTranscriptFactory.Create(messages);

        Assert.Collection(
            transcript,
            item => Assert.Equal("Read it", Assert.IsType<UserMessageViewModel>(item).Content),
            item =>
            {
                var activity = Assert.IsType<ToolActivityViewModel>(item);
                var tool = Assert.Single(activity.Calls);
                Assert.Equal("read_file", tool.ToolName);
                Assert.Equal("Succeeded", tool.Status);
                Assert.Equal("contents", tool.Output);
            },
            item =>
            {
                var assistant = Assert.IsType<AssistantMessageViewModel>(item);
                Assert.Equal("Done", assistant.Content);
                Assert.False(assistant.IsStreaming);
            },
            item =>
            {
                var marker = Assert.IsType<CompactionMarkerViewModel>(item);
                Assert.Equal("Conversation compacted", marker.Label);
                Assert.Equal("Earlier context", marker.Summary);
            });
    }

    [Fact]
    public void Create_strips_summary_prefix_from_compaction_marker()
    {
        var message = new ChatMessage(
            ChatRole.User,
            MessageKind.Summary,
            "Earlier conversation summary:\nWe discussed VRAM reporting.");

        var marker = Assert.IsType<CompactionMarkerViewModel>(
            Assert.Single(PersistedTranscriptFactory.Create([message])));

        Assert.Equal("Conversation compacted", marker.Label);
        Assert.Equal("We discussed VRAM reporting.", marker.Summary);
    }

    [Fact]
    public void Create_rebuilds_orphaned_failed_tool_result()
    {
        var call = new ToolCall(
            "call-2",
            "write_file",
            JsonSerializer.SerializeToElement(new { path = "out.txt", content = "x" }));

        var transcript = PersistedTranscriptFactory.Create(
            [ChatMessage.FromToolResult(call, new ToolResult(false, "denied"))]);

        var activity = Assert.IsType<ToolActivityViewModel>(Assert.Single(transcript));
        var tool = Assert.Single(activity.Calls);
        Assert.Equal("Failed", tool.Status);
        Assert.Equal("denied", tool.Output);
        Assert.True(tool.IsExpanded);
    }

    [Fact]
    public void Create_marks_denied_tool_result_denied_and_counts_it_as_failure()
    {
        var call = new ToolCall(
            "call-7",
            "bash",
            JsonSerializer.SerializeToElement(new { command = "rm -rf /" }));

        var transcript = PersistedTranscriptFactory.Create(
            [ChatMessage.FromToolCall(call), ChatMessage.FromToolResult(call, ToolResult.Denied)]);

        var activity = Assert.IsType<ToolActivityViewModel>(Assert.Single(transcript));
        var tool = Assert.Single(activity.Calls);
        Assert.Equal("Denied", tool.Status);
        Assert.True(tool.IsExpanded);
        Assert.True(activity.HasFailure);
        Assert.Contains("1 failed", activity.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_marks_payloadless_tool_result_completed_without_guessing_success()
    {
        var message = new ChatMessage(
            ChatRole.Tool,
            MessageKind.ToolResult,
            "Output containing ERROR] as ordinary text",
            "call-3",
            "read_file");

        var activity = Assert.IsType<ToolActivityViewModel>(Assert.Single(PersistedTranscriptFactory.Create([message])));
        var tool = Assert.Single(activity.Calls);

        Assert.Equal("Completed", tool.Status);
        Assert.False(tool.IsExpanded);
    }

    [Fact]
    public void Create_labels_context_reset_as_context_cleared()
    {
        var message = new ChatMessage(
            ChatRole.System,
            MessageKind.Summary,
            AgentRunner.ContextResetMarker);

        var marker = Assert.IsType<CompactionMarkerViewModel>(Assert.Single(PersistedTranscriptFactory.Create([message])));

        Assert.Equal("Context cleared", marker.Label);
    }

    [Fact]
    public void Create_rebuilds_persisted_file_diff()
    {
        var call = new ToolCall(
            "call-4",
            "write_file",
            JsonSerializer.SerializeToElement(new { path = "out.txt", content = "new" }));
        var result = new ToolResult(
            true,
            "Wrote out.txt",
            new FileChange("out.txt", "old", "new"));

        var activity = Assert.IsType<ToolActivityViewModel>(Assert.Single(
            PersistedTranscriptFactory.Create([ChatMessage.FromToolResult(call, result)])));
        var tool = Assert.Single(activity.Calls);

        Assert.True(tool.HasDiff);
        Assert.Equal("out.txt", tool.Diff!.Path);
    }

    [Fact]
    public void Create_groups_consecutive_tool_calls_into_one_activity()
    {
        var readCall = new ToolCall(
            "call-5",
            "read_file",
            JsonSerializer.SerializeToElement(new { path = "a.txt" }));
        var grepCall = new ToolCall(
            "call-6",
            "grep",
            JsonSerializer.SerializeToElement(new { pattern = "TODO" }));
        ChatMessage[] messages =
        [
            ChatMessage.FromToolCall(readCall),
            ChatMessage.FromToolResult(readCall, new ToolResult(true, "contents")),
            ChatMessage.FromToolCall(grepCall),
            ChatMessage.FromToolResult(grepCall, new ToolResult(true, "matches")),
        ];

        var activity = Assert.IsType<ToolActivityViewModel>(Assert.Single(PersistedTranscriptFactory.Create(messages)));

        Assert.Equal(2, activity.Calls.Count);
    }
}
