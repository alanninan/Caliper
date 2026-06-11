// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Caliper.Core.Tools.Mcp;

public sealed class McpHub(
    IOptions<McpOptions> options,
    IOptions<CaliperOptions> caliperOptions,
    ILoggerFactory loggerFactory,
    ILogger<McpHub> logger) : IMcpHub, IAsyncDisposable
{
    private static readonly TimeSpan s_serverConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<McpClient> _clients = [];
    private IReadOnlyList<ITool> _tools = [];
    private IReadOnlyList<McpServerStatus> _status = [];

    public IReadOnlyList<ITool> Tools => _tools;
    public IReadOnlyList<McpServerStatus> Status => _status;

    public async Task ConnectAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var provisionalClients = new List<McpClient>();
        try
        {
            var tools = new List<ITool>();
            var statuses = new List<McpServerStatus>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (serverName, serverOptions) in options.Value.Servers)
            {
                var connection = await ConnectServerWithToolsAsync(serverName, serverOptions, ct).ConfigureAwait(false);
                if (connection.Client is null)
                {
                    statuses.Add(new McpServerStatus(serverName, Connected: false, ToolCount: 0, Error: connection.Error));
                    continue;
                }

                provisionalClients.Add(connection.Client);
                var registered = 0;
                foreach (var tool in connection.Tools)
                {
                    var adapter = new McpToolAdapter(serverName, tool, caliperOptions);
                    if (!names.Add(adapter.Name))
                    {
                        logger.LogWarning("Skipping duplicate MCP tool name '{ToolName}' from server '{Server}'.", adapter.Name, serverName);
                        continue;
                    }

                    tools.Add(adapter);
                    registered++;
                }

                statuses.Add(new McpServerStatus(serverName, Connected: true, registered, Error: null));
            }

            var oldClients = _clients.ToArray();
            _clients.Clear();
            _clients.AddRange(provisionalClients);
            provisionalClients = [];
            _tools = tools;
            _status = statuses;

            await DisposeClientsAsync(oldClients).ConfigureAwait(false);
        }
        catch
        {
            await DisposeClientsAsync(provisionalClients).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisposeAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeClientsAsync(_clients.ToArray()).ConfigureAwait(false);
            _clients.Clear();
            _tools = [];
            _status = [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAllAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task<McpClient> ConnectServerAsync(string serverName, McpServerOptions serverOptions, CancellationToken ct)
    {
        var transport = CreateTransport(serverName, serverOptions);
        return await McpClient.CreateAsync(transport, clientOptions: new McpClientOptions(), loggerFactory, ct).ConfigureAwait(false);
    }

    private async Task<McpServerConnection> ConnectServerWithToolsAsync(
        string serverName,
        McpServerOptions serverOptions,
        CancellationToken ct)
    {
        McpClient? client = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(s_serverConnectTimeout);

            client = await ConnectServerAsync(serverName, serverOptions, timeout.Token).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            var connected = client;
            client = null;
            return new McpServerConnection(connected, [.. tools], Error: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisposeClientAsync(client).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await DisposeClientAsync(client).ConfigureAwait(false);
            return new McpServerConnection(
                Client: null,
                Tools: [],
                Error: $"MCP server '{serverName}' connect timed out after {s_serverConnectTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            await DisposeClientAsync(client).ConfigureAwait(false);
            return new McpServerConnection(Client: null, Tools: [], Error: ex.Message);
        }
        catch (Exception ex)
        {
            await DisposeClientAsync(client).ConfigureAwait(false);
            return new McpServerConnection(Client: null, Tools: [], Error: ex.Message);
        }
    }

    private IClientTransport CreateTransport(string serverName, McpServerOptions serverOptions)
    {
        var type = serverOptions.Type.Trim().ToLowerInvariant();
        return type switch
        {
            "stdio" => CreateStdioTransport(serverName, serverOptions),
            "http" or "streamable_http" or "streamable-http" => CreateHttpTransport(serverName, serverOptions),
            _ => throw new InvalidOperationException($"Unsupported MCP transport type '{serverOptions.Type}' for server '{serverName}'."),
        };
    }

    private StdioClientTransport CreateStdioTransport(string serverName, McpServerOptions serverOptions)
    {
        if (string.IsNullOrWhiteSpace(serverOptions.Command))
            throw new InvalidOperationException($"MCP server '{serverName}' requires Command for stdio transport.");

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = serverName,
            Command = serverOptions.Command,
            Arguments = [.. serverOptions.Args],
        }, loggerFactory);
    }

    private HttpClientTransport CreateHttpTransport(string serverName, McpServerOptions serverOptions)
    {
        if (!Uri.TryCreate(serverOptions.Url, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException($"MCP server '{serverName}' requires an absolute Url for HTTP transport.");

        var headers = new Dictionary<string, string>(serverOptions.Headers, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(serverOptions.BearerToken) && !headers.ContainsKey("Authorization"))
            headers["Authorization"] = $"Bearer {serverOptions.BearerToken}";

        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = serverName,
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(30),
            AdditionalHeaders = headers,
        }, loggerFactory);
    }

    private async Task DisposeClientsAsync(IEnumerable<McpClient> clients)
    {
        foreach (var client in clients)
            await DisposeClientAsync(client).ConfigureAwait(false);
    }

    private async Task DisposeClientAsync(McpClient? client)
    {
        if (client is null)
            return;

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Error disposing MCP client: {Message}", ex.Message);
        }
    }

    private sealed record McpServerConnection(
        McpClient? Client,
        IReadOnlyList<McpClientTool> Tools,
        string? Error);
}
