// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Caliper.Core.Models;

namespace Caliper.App.ViewModels;

public abstract class ChatItemViewModel : ObservableObject;

public sealed class UserMessageViewModel(string content) : ChatItemViewModel
{
    public string Content { get; } = content;
}

public sealed partial class AssistantMessageViewModel : ChatItemViewModel
{
    public bool HasContent => !string.IsNullOrWhiteSpace(Content);

    [ObservableProperty]
    public partial string Content { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStreaming { get; set; } = true;

    partial void OnContentChanged(string value) => OnPropertyChanged(nameof(HasContent));
}

public sealed partial class ReasoningViewModel : ChatItemViewModel
{
    public bool HasContent => !string.IsNullOrWhiteSpace(Content);
    public DateTimeOffset StartedAt { get; set; }
    public string DurationText => IsStreaming
        ? $"Thinking… ({ElapsedSeconds}s)"
        : $"Thought for {ElapsedSeconds}s";

    [ObservableProperty]
    public partial string Content { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStreaming { get; set; } = true;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial int ElapsedSeconds { get; set; }

    partial void OnContentChanged(string value) => OnPropertyChanged(nameof(HasContent));

    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(DurationText));

    partial void OnElapsedSecondsChanged(int value) => OnPropertyChanged(nameof(DurationText));
}

public sealed partial class ToolCallViewModel(
    string callId,
    string toolName,
    string arguments) : ChatItemViewModel
{
    public string CallId { get; } = callId;
    public string ToolName { get; } = toolName;
    public string Arguments { get; } = arguments;
    public string ArgumentsPretty { get; } = PrettyPrintJson(arguments);
    public string Headline { get; } = ComputeHeadline(toolName, arguments);
    public string InspectAutomationId => $"InspectTool_{CallId}";
    public bool HasDiff => Diff is not null;
    public bool HasOutput => !string.IsNullOrWhiteSpace(Output);
    public bool HasArguments => !string.IsNullOrWhiteSpace(Arguments);

    [ObservableProperty]
    public partial string Status { get; set; } = ToolCallStatus.Running;

    [ObservableProperty]
    public partial string Output { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial FileDiffViewModel? Diff { get; set; }

    partial void OnOutputChanged(string value) => OnPropertyChanged(nameof(HasOutput));

    partial void OnDiffChanged(FileDiffViewModel? value) => OnPropertyChanged(nameof(HasDiff));

    public void SetFileChange(FileChange? change) =>
        Diff = change is null ? null : FileDiffViewModel.Create(change);

    private static readonly JsonSerializerOptions s_prettyJson = new() { WriteIndented = true };

    // Indent the raw call arguments for the inspector's Arguments tab; falls back to the raw text
    // for anything that isn't valid JSON.
    private static string PrettyPrintJson(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            return JsonSerializer.Serialize(document.RootElement, s_prettyJson);
        }
        catch (JsonException)
        {
            return arguments;
        }
    }

    private static string ComputeHeadline(string toolName, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return toolName;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            var root = document.RootElement;
            var argumentProperty = toolName switch
            {
                "bash" or "powershell" => "command",
                "edit_file" or "write_file" or "read_file" or "list_dir" => "path",
                "grep" or "glob" => "pattern",
                "fetch_url" => "url",
                "search" => "query",
                "load_skill" => "name",
                _ => null,
            };

            if (argumentProperty is not null &&
                root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(argumentProperty, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return Summarize(value.GetString());
            }
        }
        catch (JsonException)
        {
            // Malformed/partial arguments (e.g. a mid-stream tool call) just fall back to the tool
            // name for the headline — the raw arguments are still shown in the inspector.
        }

        return toolName;
    }

    private static string Summarize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length > 80 ? oneLine[..80] + "…" : oneLine;
    }
}

public sealed partial class ToolActivityViewModel : ChatItemViewModel
{
    public ObservableCollection<ToolCallViewModel> Calls { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool HasFailure { get; set; }

    public void Add(ToolCallViewModel call)
    {
        Calls.Add(call);
        call.PropertyChanged += Call_PropertyChanged;
        Refresh();
    }

    private void Call_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolCallViewModel.Status))
            Refresh();
    }

    private void Refresh()
    {
        var total = Calls.Count;
        var running = Calls.Count(c => c.Status == ToolCallStatus.Running);
        // A denial counts as a failure for summary purposes — the call did not run. The reload path
        // has always produced "Denied" statuses, so counting it here keeps the live and reloaded
        // summaries (and the failure icon/auto-expand) consistent.
        var failed = Calls.Count(c => c.Status is ToolCallStatus.Failed or ToolCallStatus.Denied);
        IsRunning = running > 0;
        HasFailure = failed > 0;
        if (HasFailure)
            IsExpanded = true;

        var noun = total == 1 ? "call" : "calls";
        var headlines = string.Join(", ", Calls.Select(c => c.Headline).Distinct());
        Summary = running > 0
            ? $"{total} {noun} running – {headlines}"
            : failed > 0
                ? $"{total} {noun}, {failed} failed – {headlines}"
                : $"{total} {noun} – {headlines}";
    }
}

public sealed class RunStatusViewModel(string title, string message, bool isError = false) : ChatItemViewModel
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public bool IsError { get; } = isError;
}

// A full-width divider marking a context boundary in the transcript (compaction or clear), so a
// reader can tell at a glance where earlier turns stopped being sent to the model.
public sealed partial class CompactionMarkerViewModel(
    string label,
    string detail = "",
    string summary = "") : ChatItemViewModel
{
    public string Label { get; } = label;
    public string Detail { get; } = detail;
    public string Summary { get; } = summary;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}
