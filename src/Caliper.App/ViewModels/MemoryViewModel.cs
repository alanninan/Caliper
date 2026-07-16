// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using Caliper.Core.Abstractions;
using Caliper.Core.Memory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;

namespace Caliper.App.ViewModels;

public sealed partial class MemoryViewModel(
    IMemoryStore memoryStore,
    ICaliperMdProvider caliperMdProvider,
    IRuntimeSettings runtimeSettings) : ObservableObject
{
    public ObservableCollection<MemoryItemViewModel> Memories { get; } = [];

    [ObservableProperty]
    public partial string ProjectDocumentPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProjectDocument { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    // U10: add/edit form fields. Index 0 = Global, 1 = Project (see MemoryScopePicker in the page).
    [ObservableProperty]
    public partial int MemoryScopeIndex { get; set; }

    [ObservableProperty]
    public partial string MemoryKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MemoryValue { get; set; } = string.Empty;

    public bool HasMemories => Memories.Count > 0;
    public string MemoryCountText => $"{Memories.Count:N0}";

    [RelayCommand]
    private async Task RefreshMemoryAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await ReloadAsync();
            StatusMessage = $"{Memories.Count:N0} memory entries loaded.";
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // A11: bounded by the real failure surface of the two calls above — SqliteMemoryStore
            // (SQLite reads) and CaliperMdProvider (a plain File.ReadAllTextAsync).
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // U10: per-row Forget, confirmed via an inline Flyout in the page (not an immediate delete).
    // Failure ordering: the store call happens before the reload, so a failure never touches the
    // list — Memories still reflects the last successful load.
    [RelayCommand]
    private async Task ForgetAsync(MemoryItemViewModel item)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await memoryStore.ForgetAsync(item.Scope, item.Key, CancellationToken.None);
            await ReloadAsync();
            StatusMessage = $"Forgot '{item.Key}'.";
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // U10: add/edit form. RememberAsync upserts (SqliteMemoryStore does INSERT ... ON CONFLICT
    // UPDATE on scope+key), so saving a prefilled form (see PrefillFromEntry) is the edit path —
    // there's no separate typed "update" call. Failure ordering: fields are cleared only after the
    // store call and reload both succeed; a failure leaves the user's input in place.
    [RelayCommand]
    private async Task RememberAsync()
    {
        var key = MemoryKey.Trim();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(MemoryValue))
        {
            StatusMessage = "Enter both a key and a value before remembering.";
            return;
        }

        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var scope = MemoryScopeIndex == 1
                ? MemoryScope.Project(runtimeSettings.Caliper.WorkingRoot)
                : MemoryScope.Global;
            await memoryStore.RememberAsync(scope, key, MemoryValue, CancellationToken.None);
            await ReloadAsync();
            StatusMessage = $"Saved '{key}'.";
            MemoryKey = string.Empty;
            MemoryValue = string.Empty;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // U10: copies a row's scope/key/value into the add/edit form fields; the page expands
    // AddMemoryExpander and calls this from the row's Edit button. Scope index mapping mirrors
    // RememberAsync's own Global/Project split above.
    public void PrefillFromEntry(MemoryItemViewModel item)
    {
        MemoryScopeIndex = string.Equals(item.Scope, MemoryScope.Global, StringComparison.Ordinal) ? 0 : 1;
        MemoryKey = item.Key;
        MemoryValue = item.Value;
    }

    // Shared by RefreshMemoryAsync, ForgetAsync, and RememberAsync — each manages its own IsBusy
    // guard/finally and catch around this core reload so the busy state and error surface stay
    // consistent no matter which command triggered it.
    private async Task ReloadAsync()
    {
        var options = runtimeSettings.Caliper;
        var projectScope = MemoryScope.Project(options.WorkingRoot);
        var global = await memoryStore.RecallAsync(MemoryScope.Global, query: null, CancellationToken.None);
        var project = await memoryStore.RecallAsync(projectScope, query: null, CancellationToken.None);
        Memories.Clear();
        foreach (var entry in global.Concat(project).OrderByDescending(static item => item.UpdatedAt))
            Memories.Add(new MemoryItemViewModel(entry));

        var document = await caliperMdProvider.ReadAsync(options.WorkingRoot, CancellationToken.None);
        ProjectDocumentPath = document.Path;
        ProjectDocument = string.IsNullOrWhiteSpace(document.Content)
            ? "No project memory document was found."
            : document.Content + (document.Truncated ? "\n\n[Preview truncated]" : string.Empty);
        OnPropertyChanged(nameof(HasMemories));
        OnPropertyChanged(nameof(MemoryCountText));
    }
}

public sealed record MemoryItemViewModel(string Scope, string Key, string Value, string Updated)
{
    public MemoryItemViewModel(MemoryEntry entry)
        : this(entry.Scope, entry.Key, entry.Value, entry.UpdatedAt.ToLocalTime().ToString("g")) { }

    // U10: mirrors SessionItemViewModel's per-row automation ids. Scope+Key is unique per entry
    // (SqliteMemoryStore upserts on that pair), so a sanitized combination is a stable,
    // collision-free id across rows without needing a separate identity field.
    public string EditAutomationId => $"EditMemory_{SanitizedId}";
    public string ForgetAutomationId => $"ForgetMemory_{SanitizedId}";
    public string ConfirmForgetAutomationId => $"ConfirmForgetMemory_{SanitizedId}";
    public string ForgetConfirmText => $"Forget '{Key}'?";

    private string SanitizedId => Sanitize($"{Scope}_{Key}");

    private static string Sanitize(string value) =>
        new([.. value.Select(static c => char.IsLetterOrDigit(c) ? c : '_')]);
}
