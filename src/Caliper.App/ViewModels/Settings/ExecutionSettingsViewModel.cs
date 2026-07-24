// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels.Settings;

/// <summary>
/// P4: settings surface for <c>Caliper:Execution</c> (roadmap §3.3 sandboxed shell execution).
/// String-backed Backend/Network — mirrors <c>McpServerSettingViewModel.Type</c>'s identical
/// string-ComboBox pattern — so the detail form can use plain <c>&lt;x:String&gt;</c> items in
/// XAML. The whole section is a live seam (see <see cref="IConfigWriter.SaveExecutionAsync"/>'s
/// doc comment), so unlike some other settings pages this never actually needs a restart — kept
/// anyway for uniformity with every other settings page rather than special-cased away.
/// </summary>
public sealed partial class ExecutionSettingsViewModel(IConfigWriter configWriter) : ObservableObject
{
    public IReadOnlyList<string> BackendOptions { get; } = [nameof(ExecutionBackendKind.Host), nameof(ExecutionBackendKind.Container)];
    public IReadOnlyList<string> NetworkOptions { get; } = [nameof(ExecutionNetworkKind.None), nameof(ExecutionNetworkKind.Bridge)];

    [ObservableProperty] public partial string SelectedBackend { get; set; } = nameof(ExecutionBackendKind.Host);
    [ObservableProperty] public partial string Image { get; set; } = string.Empty;
    [ObservableProperty] public partial string SelectedNetwork { get; set; } = nameof(ExecutionNetworkKind.None);
    [ObservableProperty] public partial double Cpus { get; set; }
    [ObservableProperty] public partial double MemoryMb { get; set; }
    [ObservableProperty] public partial string User { get; set; } = string.Empty;

    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool StatusIsError { get; set; }
    [ObservableProperty] public partial bool RestartRequired { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    private Snapshot? _snapshot;

    /// <summary>
    /// Non-empty only when <see cref="SelectedBackend"/> is Host and a bare <c>"*"</c> wildcard is
    /// present in either the global Permissions allowlist or a schedule overlay's — proactive
    /// surfacing of the roadmap §3.3 rule (<c>UnattendedAllowlistGuard</c>) shown before Save is
    /// clicked, rather than letting the validator's rejection be the first signal.
    /// </summary>
    [ObservableProperty] public partial string WildcardWarningText { get; set; } = string.Empty;

    /// <summary>True when a bare "*" was found anywhere at the last <see cref="RefreshWildcardSourceAsync"/> — cached so flipping the Backend ComboBox re-derives the warning without re-hitting disk on every selection change.</summary>
    private bool _hasWildcardAllowlist;

    public bool IsContainerBackend =>
        string.Equals(SelectedBackend, nameof(ExecutionBackendKind.Container), StringComparison.Ordinal);

    public bool IsHostBackend => !IsContainerBackend;

    partial void OnSelectedBackendChanged(string value)
    {
        OnPropertyChanged(nameof(IsContainerBackend));
        OnPropertyChanged(nameof(IsHostBackend));
        UpdateWildcardWarning();
        UpdateDirty();
    }
    partial void OnImageChanged(string value) => UpdateDirty();
    partial void OnSelectedNetworkChanged(string value) => UpdateDirty();
    partial void OnCpusChanged(double value) => UpdateDirty();
    partial void OnMemoryMbChanged(double value) => UpdateDirty();
    partial void OnUserChanged(string value) => UpdateDirty();

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        var execution = await configWriter.LoadExecutionAsync(ct);
        SelectedBackend = execution.Backend.ToString();
        Image = execution.Image;
        SelectedNetwork = execution.Network.ToString();
        Cpus = execution.Cpus;
        MemoryMb = execution.MemoryMb;
        User = execution.User;

        await RefreshWildcardSourceAsync(ct);
        _snapshot = Capture();
        IsDirty = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        RestartRequired = false;
        var backend = Enum.TryParse<ExecutionBackendKind>(SelectedBackend, out var parsedBackend)
            ? parsedBackend
            : ExecutionBackendKind.Host;
        var network = Enum.TryParse<ExecutionNetworkKind>(SelectedNetwork, out var parsedNetwork)
            ? parsedNetwork
            : ExecutionNetworkKind.None;

        var execution = new ExecutionOptions
        {
            Backend = backend,
            Image = Image,
            Network = network,
            Cpus = Cpus,
            MemoryMb = (int)MemoryMb,
            User = User,
        };

        var result = await configWriter.SaveExecutionAsync(execution, CancellationToken.None);
        StatusIsError = !result.Success;
        RestartRequired = result.Success && result.RestartRequired;
        StatusMessage = result.Success
            ? "Saved. Changes apply to the next shell call."
            : result.Error ?? "Save failed.";

        if (result.Success)
        {
            await RefreshWildcardSourceAsync(CancellationToken.None);
            _snapshot = Capture();
            IsDirty = false;
        }
    }

    [RelayCommand]
    private async Task DiscardAsync(CancellationToken ct)
    {
        StatusMessage = string.Empty;
        StatusIsError = false;
        await LoadAsync(ct);
    }

    private Snapshot Capture() => new(SelectedBackend, Image, SelectedNetwork, Cpus, MemoryMb, User);
    private void UpdateDirty()
    {
        if (_snapshot is not null)
            IsDirty = Capture() != _snapshot;
    }
    private sealed record Snapshot(string Backend, string Image, string Network, double Cpus, double Memory, string User);

    /// <summary>
    /// Reads the current Permissions section and Schedules list once (on Load and after a
    /// successful Save) and caches whether a bare wildcard exists anywhere in them. A11: a failed
    /// read of either section must never crash this page or block loading the Execution form — a
    /// read failure is treated as "no warning", per spec, not as a page-load failure.
    /// </summary>
    private async Task RefreshWildcardSourceAsync(CancellationToken ct)
    {
        _hasWildcardAllowlist = false;
        try
        {
            var permissions = await configWriter.LoadPermissionsAsync(ct);
            _hasWildcardAllowlist = HasBareWildcard(permissions.ShellAutoAllowlist);

            if (!_hasWildcardAllowlist)
            {
                var schedules = await configWriter.LoadSchedulesAsync(ct);
                _hasWildcardAllowlist = schedules.Any(schedule =>
                    schedule.Permissions is { } overlay && HasBareWildcard(overlay.ShellAutoAllowlist));
            }
        }
        catch (Exception)
        {
            _hasWildcardAllowlist = false;
        }

        UpdateWildcardWarning();
    }

    private void UpdateWildcardWarning()
    {
        WildcardWarningText = _hasWildcardAllowlist && IsHostBackend
            ? "A wildcard shell allowlist requires the Container backend — saving Host will fail validation."
            : string.Empty;
    }

    // Mirrors UnattendedAllowlistGuard.HasBareWildcard (Caliper.Core, internal to that assembly) —
    // duplicated here rather than exposed across the assembly boundary since this is a proactive
    // UI hint only; SaveExecutionAsync/SaveSchedulesAsync still run the authoritative check.
    private static bool HasBareWildcard(IEnumerable<string> allowlist) =>
        allowlist.Any(entry => entry?.Trim() == "*");
}
