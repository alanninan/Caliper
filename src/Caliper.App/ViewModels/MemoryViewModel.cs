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
            StatusMessage = $"{Memories.Count:N0} memory entries loaded.";
            OnPropertyChanged(nameof(HasMemories));
            OnPropertyChanged(nameof(MemoryCountText));
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
}

public sealed record MemoryItemViewModel(string Scope, string Key, string Value, string Updated)
{
    public MemoryItemViewModel(MemoryEntry entry)
        : this(entry.Scope, entry.Key, entry.Value, entry.UpdatedAt.ToLocalTime().ToString("g")) { }
}
