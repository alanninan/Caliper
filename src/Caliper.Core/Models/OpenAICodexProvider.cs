// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Protocol;
using Caliper.Core.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Caliper.Core.Models;

#pragma warning disable OPENAI001 // Responses SDK/MEAI adapter is required for the Codex Responses transport.
/// <summary>
/// ChatGPT subscription provider. OAuth is intentionally separate from OpenAI Platform API-key
/// auth even though both use the Responses item model.
/// </summary>
internal sealed class OpenAICodexProvider(
    IOptions<ProvidersOptions> providerOptions,
    OpenAICodexAuthService auth,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IModelProvider
{
    private const int FallbackContextWindow = 272_000;
    public const string HttpClientName = "openai-codex-meta";

    public string Id => ProviderIds.OpenAICodex;
    public string DisplayName => "OpenAI Codex";
    public ProviderAuthenticationKind AuthenticationKind => ProviderAuthenticationKind.OAuth;

    public IChatClient GetClient(string modelSlug)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(providerOptions.Value.OpenAICodex.Endpoint),
        };
        var responses = new ResponsesClient(new CodexAuthenticationPolicy(auth), options);
        IChatClient model = new CodexCompatibilityChatClient(responses.AsIChatClient(modelSlug));
        return new ChatClientBuilder(model)
            .UseLogging(loggerFactory)
            .UseOpenTelemetry(loggerFactory, sourceName: "Caliper")
            .Build();
    }

    public Task<ModelCapabilities> GetAsync(string modelSlug, CancellationToken ct) =>
        Task.FromResult(Fallback());

    public async Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(CancellationToken ct)
    {
        var token = await auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        var endpoint = providerOptions.Value.OpenAICodex.Endpoint.TrimEnd('/');
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{endpoint}/models?client_version=1.0.0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        AddHeaders(request, token.AccountId);

        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync(
            CaliperJsonContext.Default.CodexModelsResponse,
            ct).ConfigureAwait(false);

        return (payload?.Models ?? [])
            .Where(model => !string.IsNullOrWhiteSpace(model.Slug))
            .Select(model => new ModelCatalogEntry(
                model.Slug,
                new ModelCapabilities(
                    SupportsTools: true,
                    SupportsReasoning: model.SupportedReasoningLevels is { Count: > 0 },
                    SupportsStructuredOutputs: true,
                    ContextWindowTokens: model.ContextWindow.GetValueOrDefault(FallbackContextWindow))))
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ModelCapabilities Fallback() =>
        new(
            SupportsTools: true,
            SupportsReasoning: true,
            SupportsStructuredOutputs: true,
            ContextWindowTokens: FallbackContextWindow);

    /// <summary>
    /// The ChatGPT Codex endpoint accepts the Responses request shape used by the Codex clients,
    /// which intentionally omits generic sampling and output-limit fields. Keep this adaptation at
    /// the provider boundary so OpenAI Platform and the other providers retain their generation
    /// settings.
    /// </summary>
    internal static void ConfigureCodexOptions(ChatOptions options)
    {
        options.Temperature = null;
        options.TopP = null;
        options.Seed = null;
        options.MaxOutputTokens = null;

        var previousFactory = options.RawRepresentationFactory;
        options.RawRepresentationFactory = client =>
        {
            var responseOptions = previousFactory?.Invoke(client) as CreateResponseOptions ?? new();
            responseOptions.StoredOutputEnabled = false;
            if (!responseOptions.IncludedProperties.Contains(
                    IncludedResponseProperty.ReasoningEncryptedContent))
            {
                responseOptions.IncludedProperties.Add(
                    IncludedResponseProperty.ReasoningEncryptedContent);
            }

            return responseOptions;
        };
    }

    internal static IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> MoveSystemMessagesToInstructions(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions options)
    {
        var input = new List<Microsoft.Extensions.AI.ChatMessage>();
        var instructions = new List<string>();
        foreach (var message in messages)
        {
            if (message.Role.Equals(Microsoft.Extensions.AI.ChatRole.System))
            {
                if (!string.IsNullOrWhiteSpace(message.Text))
                    instructions.Add(message.Text);
                continue;
            }

            input.Add(message);
        }

        if (!string.IsNullOrWhiteSpace(options.Instructions))
            instructions.Add(options.Instructions);

        options.Instructions = instructions.Count == 0
            ? "You are Caliper, a helpful AI assistant."
            : string.Join(Environment.NewLine, instructions);
        return input;
    }

    private static void AddHeaders(HttpRequestMessage request, string? accountId)
    {
        request.Headers.TryAddWithoutValidation("originator", "caliper");
        request.Headers.TryAddWithoutValidation("User-Agent", "caliper/1.0");
        request.Headers.TryAddWithoutValidation("version", "1.0.0");
        if (!string.IsNullOrWhiteSpace(accountId))
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
    }

    private sealed class CodexAuthenticationPolicy(OpenAICodexAuthService auth) : AuthenticationPolicy
    {
        public override void Process(
            PipelineMessage message,
            IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex)
        {
            var token = auth.GetAccessTokenAsync(message.CancellationToken)
                .GetAwaiter()
                .GetResult();
            AddHeaders(message, token);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override async ValueTask ProcessAsync(
            PipelineMessage message,
            IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex)
        {
            var token = await auth.GetAccessTokenAsync(message.CancellationToken).ConfigureAwait(false);
            AddHeaders(message, token);
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        }

        private static void AddHeaders(PipelineMessage message, CodexAccessToken token)
        {
            message.Request.Headers.Set("Authorization", $"Bearer {token.AccessToken}");
            message.Request.Headers.Set("originator", "caliper");
            message.Request.Headers.Set("User-Agent", "caliper/1.0");
            message.Request.Headers.Set("version", "1.0.0");
            if (!string.IsNullOrWhiteSpace(token.AccountId))
                message.Request.Headers.Set("ChatGPT-Account-Id", token.AccountId);
        }
    }

    private sealed class CodexCompatibilityChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options = options?.Clone() ?? new();
            ConfigureCodexOptions(options);
            messages = MoveSystemMessagesToInstructions(messages, options);
            try
            {
                return await base.GetResponseAsync(messages, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ClientResultException ex)
            {
                throw Enrich(ex);
            }
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            options = options?.Clone() ?? new();
            ConfigureCodexOptions(options);
            messages = MoveSystemMessagesToInstructions(messages, options);
            await using var enumerator = base.GetStreamingResponseAsync(
                    messages,
                    options,
                    cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        yield break;
                    update = enumerator.Current;
                }
                catch (ClientResultException ex)
                {
                    throw Enrich(ex);
                }

                yield return update;
            }
        }

        private static Exception Enrich(ClientResultException exception)
        {
            var detail = TryGetErrorDetail(exception);
            return string.IsNullOrWhiteSpace(detail)
                ? exception
                : new InvalidOperationException(
                    $"OpenAI Codex request failed ({exception.Status}): {detail}",
                    exception);
        }

        private static string? TryGetErrorDetail(ClientResultException exception)
        {
            try
            {
                var content = exception.GetRawResponse()?.Content.ToString();
                if (string.IsNullOrWhiteSpace(content))
                    return null;

                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;
                var message = root.TryGetProperty("error", out var error) &&
                              error.ValueKind == JsonValueKind.Object &&
                              error.TryGetProperty("message", out var nestedMessage)
                    ? nestedMessage.GetString()
                    : root.TryGetProperty("detail", out var detail)
                        ? detail.GetString()
                        : root.TryGetProperty("message", out var topLevelMessage)
                            ? topLevelMessage.GetString()
                            : null;

                if (string.IsNullOrWhiteSpace(message))
                    return null;

                const int maxDetailLength = 1_000;
                var normalized = message.ReplaceLineEndings(" ").Trim();
                return normalized.Length <= maxDetailLength
                    ? normalized
                    : $"{normalized[..maxDetailLength]}…";
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
#pragma warning restore OPENAI001
