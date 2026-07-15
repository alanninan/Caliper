// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using Caliper.App.Permissions;
using Caliper.App.Security;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels.Settings;

public sealed partial class McpServersSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IMcpHub _mcpHub;
    private readonly IConfigWriter _configWriter;
    private readonly IUiDispatcher _dispatcher;
    private readonly ICredentialStore _credentials;

    public McpServersSettingsViewModel(
        IMcpHub mcpHub,
        IConfigWriter configWriter,
        IUiDispatcher dispatcher,
        ICredentialStore credentials)
    {
        _mcpHub = mcpHub;
        _configWriter = configWriter;
        _dispatcher = dispatcher;
        _credentials = credentials;
        _mcpHub.StatusChanged += McpHub_StatusChanged;
        RefreshStatus();
    }

    public ObservableCollection<McpStatusViewModel> McpServers { get; } = [];
    public ObservableCollection<McpServerSettingViewModel> Servers { get; } = [];
    public IReadOnlyList<string> TransportOptions { get; } = ["stdio", "http"];

    [ObservableProperty]
    public partial McpServerSettingViewModel? SelectedServer { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasMcpServers => McpServers.Count > 0;
    public string ServerCountText => $"{Servers.Count:N0}";
    public string ToolCountText => $"{McpServers.Sum(static s => s.ToolCount):N0} tools";
    public string HealthText => McpServers.Count == 0
        ? "Not configured"
        : McpServers.All(static s => s.Connected) ? "Connected" : "Needs attention";

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        var options = await _configWriter.LoadMcpAsync(ct);
        Servers.Clear();
        foreach (var (name, server) in options.Servers)
        {
            var item = McpServerSettingViewModel.FromOptions(name, server);
            if (_credentials.TryRead(CredentialTargets.McpBearerToken(name), out var token))
                item.BearerToken = token;
            Servers.Add(item);
        }

        SelectedServer = Servers.FirstOrDefault();
        OnPropertyChanged(nameof(ServerCountText));
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await _mcpHub.ConnectAllAsync(CancellationToken.None);
            StatusMessage = "MCP connections refreshed.";
        }
        catch (Exception ex)
        {
            // A11: same unenumerable MCP connection surface as App.xaml.cs's ConnectMcpAsync
            // (arbitrary user-configured external processes/HTTP endpoints).
            StatusIsError = true;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddServer()
    {
        var index = Servers.Count + 1;
        var server = new McpServerSettingViewModel { Name = $"server-{index}", Type = "stdio" };
        Servers.Add(server);
        SelectedServer = server;
        OnPropertyChanged(nameof(ServerCountText));
    }

    [RelayCommand]
    private void RemoveSelectedServer()
    {
        if (SelectedServer is null)
            return;

        _credentials.Delete(CredentialTargets.McpBearerToken(SelectedServer.Name.Trim()));
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.FirstOrDefault();
        OnPropertyChanged(nameof(ServerCountText));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var options = new McpOptions();
        foreach (var server in Servers)
        {
            var name = server.Name.Trim();
            var target = CredentialTargets.McpBearerToken(name);
            if (string.IsNullOrWhiteSpace(server.BearerToken))
                _credentials.Delete(target);
            else
                _credentials.Save(target, server.BearerToken);

            options.Servers[name] = new McpServerOptions
            {
                Type = server.Type,
                Url = string.IsNullOrWhiteSpace(server.Url) ? null : server.Url,
                Command = string.IsNullOrWhiteSpace(server.Command) ? null : server.Command,
                Args = ParseLines(server.ArgsText),
                BearerToken = null,
                Headers = ParseHeaders(server.HeadersText),
            };
        }

        var result = await _configWriter.SaveMcpAsync(options, CancellationToken.None);
        StatusIsError = !result.Success;
        StatusMessage = result.Success
            ? "Saved. Restart Caliper for server changes to take effect."
            : result.Error ?? "Save failed.";
    }

    private static string[] ParseLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Dictionary<string, string> ParseHeaders(string value)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ParseLines(value))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
                pairs[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return pairs;
    }

    private void McpHub_StatusChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(RefreshStatus);

    private void RefreshStatus()
    {
        McpServers.Clear();
        foreach (var status in _mcpHub.Status)
            McpServers.Add(new McpStatusViewModel(status));
        OnPropertyChanged(nameof(HasMcpServers));
        OnPropertyChanged(nameof(ToolCountText));
        OnPropertyChanged(nameof(HealthText));
    }

    public void Dispose() => _mcpHub.StatusChanged -= McpHub_StatusChanged;
}

public sealed class McpStatusViewModel(McpServerStatus status)
{
    public string Name { get; } = status.Name;
    public bool Connected { get; } = status.Connected;
    public int ToolCount { get; } = status.ToolCount;
    public string State { get; } = status.Connected ? "Connected" : "Disconnected";
    public string Detail { get; } = status.Error is null
        ? $"{status.ToolCount:N0} tools"
        : $"{status.ToolCount:N0} tools · {status.Error}";
}

public sealed partial class McpServerSettingViewModel : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Type { get; set; } = "stdio";
    [ObservableProperty] public partial string Url { get; set; } = string.Empty;
    [ObservableProperty] public partial string Command { get; set; } = string.Empty;
    [ObservableProperty] public partial string ArgsText { get; set; } = string.Empty;
    [ObservableProperty] public partial string BearerToken { get; set; } = string.Empty;
    [ObservableProperty] public partial string HeadersText { get; set; } = string.Empty;

    public string Summary => Type.Equals("stdio", StringComparison.OrdinalIgnoreCase)
        ? string.IsNullOrWhiteSpace(Command) ? "stdio server" : Command
        : string.IsNullOrWhiteSpace(Url) ? "HTTP server" : Url;

    public static McpServerSettingViewModel FromOptions(string name, McpServerOptions options) => new()
    {
        Name = name,
        Type = options.Type,
        Url = options.Url ?? string.Empty,
        Command = options.Command ?? string.Empty,
        ArgsText = string.Join(Environment.NewLine, options.Args),
        BearerToken = options.BearerToken ?? string.Empty,
        HeadersText = string.Join(Environment.NewLine, options.Headers.Select(static pair => $"{pair.Key}={pair.Value}")),
    };
}
