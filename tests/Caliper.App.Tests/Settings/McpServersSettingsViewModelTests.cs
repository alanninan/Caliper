// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Permissions;
using Caliper.App.ViewModels.Settings;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;

namespace Caliper.App.Tests.Settings;

public sealed class McpServersSettingsViewModelTests
{
    [Fact]
    public async Task LoadAsync_populates_servers_from_config_writer()
    {
        var configWriter = new FakeConfigWriter();
        configWriter.Mcp.Servers["local"] = new McpServerOptions { Type = "stdio", Command = "npx" };
        var viewModel = new McpServersSettingsViewModel(new FakeMcpHub(), configWriter, new InlineDispatcher(), new FakeCredentialStore());

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("local", Assert.Single(viewModel.Servers).Name);
    }

    [Fact]
    public void StatusChanged_event_refreshes_server_status()
    {
        var hub = new FakeMcpHub();
        using var viewModel = new McpServersSettingsViewModel(hub, new FakeConfigWriter(), new InlineDispatcher(), new FakeCredentialStore());

        hub.Status = [new McpServerStatus("local", true, 3, null)];
        hub.RaiseStatusChanged();

        Assert.True(viewModel.HasMcpServers);
        Assert.Equal("Connected", Assert.Single(viewModel.McpServers).State);
    }

    [Fact]
    public async Task SaveAsync_writes_edited_servers()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new McpServersSettingsViewModel(new FakeMcpHub(), configWriter, new InlineDispatcher(), new FakeCredentialStore());
        viewModel.AddServerCommand.Execute(null);
        viewModel.SelectedServer!.Command = "npx";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Single(configWriter.SavedMcp!.Servers);
    }

    [Fact]
    public void Dispose_unsubscribes_from_status_changed()
    {
        var hub = new FakeMcpHub();
        var viewModel = new McpServersSettingsViewModel(hub, new FakeConfigWriter(), new InlineDispatcher(), new FakeCredentialStore());

        viewModel.Dispose();
        hub.RaiseStatusChanged();

        Assert.False(viewModel.HasMcpServers);
    }

    [Fact]
    public async Task SaveAsync_routes_bearer_token_through_credential_store_not_config_file()
    {
        var configWriter = new FakeConfigWriter();
        var credentials = new FakeCredentialStore();
        var viewModel = new McpServersSettingsViewModel(new FakeMcpHub(), configWriter, new InlineDispatcher(), credentials);
        viewModel.AddServerCommand.Execute(null);
        viewModel.SelectedServer!.Name = "local";
        viewModel.SelectedServer!.Command = "npx";
        viewModel.SelectedServer!.BearerToken = "token-123";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(configWriter.SavedMcp!.Servers).Value.BearerToken);
        Assert.True(credentials.TryRead("Caliper/Mcp/local/BearerToken", out var storedToken));
        Assert.Equal("token-123", storedToken);
    }

    [Fact]
    public async Task LoadAsync_reads_bearer_token_from_credential_store()
    {
        var configWriter = new FakeConfigWriter();
        configWriter.Mcp.Servers["local"] = new McpServerOptions { Type = "stdio", Command = "npx" };
        var credentials = new FakeCredentialStore();
        credentials.Save("Caliper/Mcp/local/BearerToken", "stored-token");
        var viewModel = new McpServersSettingsViewModel(new FakeMcpHub(), configWriter, new InlineDispatcher(), credentials);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("stored-token", Assert.Single(viewModel.Servers).BearerToken);
    }

    [Fact]
    public void RemoveSelectedServer_deletes_its_stored_credential()
    {
        var credentials = new FakeCredentialStore();
        credentials.Save("Caliper/Mcp/local/BearerToken", "stored-token");
        var viewModel = new McpServersSettingsViewModel(new FakeMcpHub(), new FakeConfigWriter(), new InlineDispatcher(), credentials);
        viewModel.AddServerCommand.Execute(null);
        viewModel.SelectedServer!.Name = "local";

        viewModel.RemoveSelectedServerCommand.Execute(null);

        Assert.False(credentials.TryRead("Caliper/Mcp/local/BearerToken", out _));
    }

    private sealed class FakeMcpHub : IMcpHub
    {
        public IReadOnlyList<ITool> Tools => [];
        public IReadOnlyList<McpServerStatus> Status { get; set; } = [];
        public event EventHandler? StatusChanged;

        public Task ConnectAllAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisposeAllAsync() => Task.CompletedTask;
        public void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool HasThreadAccess => true;

        public bool TryEnqueue(Action action)
        {
            action();
            return true;
        }
    }
}
