// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Caliper.Core.Abstractions;
using Caliper.Core.Protocol;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Configuration;

/// <summary>
/// Typed, per-section replacement for hand-rolled JSON-string mutation of config.json. Each
/// SaveXAsync validates the incoming section, serializes it into config.json via the source-gen
/// context (no reflection, no magic string keys beyond the six fixed section names below), and
/// applies whichever fields have a live seam through <see cref="IRuntimeSettings"/>.
/// </summary>
internal sealed class ConfigWriter(
    IConfigFileStore fileStore,
    IRuntimeSettings runtimeSettings,
    IValidateOptions<CaliperOptions> caliperValidator,
    IValidateOptions<SearchOptions> searchValidator) : IConfigWriter
{
    private const string CaliperSection = "Caliper";
    private const string PermissionsSection = "Permissions";
    private const string ProvidersSection = "Providers";
    private const string McpSection = "Mcp";
    private const string SearchSection = "Search";
    private const string PersistenceSection = "Persistence";

    private static readonly JsonDocumentOptions s_documentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public async Task<CaliperOptions> LoadCaliperAsync(CancellationToken ct) =>
        ReadSection(await ReadRootAsync(ct).ConfigureAwait(false), CaliperSection, CaliperJsonContext.Default.CaliperOptions)
            ?? new CaliperOptions();

    public async Task<PermissionsOptions> LoadPermissionsAsync(CancellationToken ct) =>
        ReadSection(await ReadRootAsync(ct).ConfigureAwait(false), PermissionsSection, CaliperJsonContext.Default.PermissionsOptions)
            ?? new PermissionsOptions();

    public async Task<ProvidersOptions> LoadProvidersAsync(CancellationToken ct) =>
        ReadSection(await ReadRootAsync(ct).ConfigureAwait(false), ProvidersSection, CaliperJsonContext.Default.ProvidersOptions)
            ?? new ProvidersOptions();

    public async Task<McpOptions> LoadMcpAsync(CancellationToken ct) =>
        ReadSection(await ReadRootAsync(ct).ConfigureAwait(false), McpSection, CaliperJsonContext.Default.McpOptions)
            ?? new McpOptions();

    public async Task<SearchOptions> LoadSearchAsync(CancellationToken ct) =>
        ReadSection(await ReadRootAsync(ct).ConfigureAwait(false), SearchSection, CaliperJsonContext.Default.SearchOptions)
            ?? new SearchOptions();

    public async Task<PersistenceOptions> LoadPersistenceAsync(CancellationToken ct) =>
        ReadSection(await ReadRootAsync(ct).ConfigureAwait(false), PersistenceSection, CaliperJsonContext.Default.PersistenceOptions)
            ?? new PersistenceOptions();

    public async Task<SubagentsOptions> LoadSubagentsAsync(CancellationToken ct) =>
        (await LoadCaliperAsync(ct).ConfigureAwait(false)).Subagents;

    public async Task<IList<ScheduleOptions>> LoadSchedulesAsync(CancellationToken ct) =>
        (await LoadCaliperAsync(ct).ConfigureAwait(false)).Schedules;

    public async Task<ExecutionOptions> LoadExecutionAsync(CancellationToken ct) =>
        (await LoadCaliperAsync(ct).ConfigureAwait(false)).Execution;

    public async Task<ConfigWriteResult> SaveCaliperAsync(CaliperOptions value, CancellationToken ct)
    {
        var validation = caliperValidator.Validate(null, value);
        if (validation.Failed)
            return Invalid(validation);

        var root = await ReadRootAsync(ct).ConfigureAwait(false);
        var previous = ReadSection(root, CaliperSection, CaliperJsonContext.Default.CaliperOptions) ?? new CaliperOptions();
        root[CaliperSection] = JsonSerializer.SerializeToNode(value, CaliperJsonContext.Default.CaliperOptions);
        await WriteRootAsync(root, ct).ConfigureAwait(false);

        // Fields AgentRunner/TurnStrategySelector/ChatSummarizer read from the live
        // runtimeSettings.Caliper clone (fresh at the start of every run and every step) apply
        // immediately. EnabledTools/SkillsDirectory/SkillSelector are bound once into separate
        // DI singletons (ToolRegistry, SkillStore, the ISkillSelector chosen at startup) and
        // cannot be made live without restarting.
        runtimeSettings.UpdateCaliper(c =>
        {
            c.Provider = value.Provider;
            c.Model = value.Model;
            c.SummarizerModel = value.SummarizerModel;
            c.Temperature = value.Temperature;
            c.Seed = value.Seed;
            c.Reasoning.Effort = value.Reasoning.Effort;
            c.Reasoning.Exclude = value.Reasoning.Exclude;
            c.TurnStrategy = value.TurnStrategy;
            c.MaxSteps = value.MaxSteps;
            c.DuplicateCallLimit = value.DuplicateCallLimit;
            c.ToolTimeoutSeconds = value.ToolTimeoutSeconds;
            c.ToolMaxRetries = value.ToolMaxRetries;
            c.ToolOutputMaxChars = value.ToolOutputMaxChars;
            c.WorkingRoot = value.WorkingRoot;
            c.MaxSurfacedSkills = value.MaxSurfacedSkills;
            c.Context.AutoCompact = value.Context.AutoCompact;
            c.Context.CompactAtFraction = value.Context.CompactAtFraction;
            c.Context.KeepRecentTurns = value.Context.KeepRecentTurns;
            c.Context.ReservedOutputTokens = value.Context.ReservedOutputTokens;
            c.Memory.Enabled = value.Memory.Enabled;
            c.Memory.GlobalDir = value.Memory.GlobalDir;
            c.Memory.ProjectFile = value.Memory.ProjectFile;
            c.Subagents = CloneSubagents(value.Subagents);
            // Schedules is a live seam: SchedulerHostedService re-reads the job list from
            // runtimeSettings.Caliper.Schedules on every tick (and its sleep is interrupted by
            // SettingsChanged), so saved add/edit/remove/enable changes apply without a restart.
            // Scheduler.MaxConcurrentJobs is the exception — it sizes the cross-job semaphore once
            // at scheduler start, so it's counted in restartRequired below.
            c.Scheduler.MaxConcurrentJobs = value.Scheduler.MaxConcurrentJobs;
            c.Schedules = RuntimeSettings.CloneSchedules(value.Schedules);
            // Execution is entirely live (roadmap §3.3): ShellTool re-reads
            // runtimeSettings.Caliper.Execution.Backend per call, and both HostExecutionBackend and
            // ContainerExecutionBackend are always-constructed DI singletons, so flipping Backend or
            // tuning Image/Network/Cpus/MemoryMb/User never needs a restart. The only thing that
            // *is* cached across calls is ContainerExecutionBackend's docker-availability probe
            // result (a short TimeProvider-driven TTL, not tied to this save path) — a save here
            // doesn't need to (and can't cheaply) invalidate that cache; a stale "unavailable"
            // verdict self-heals within the TTL.
            c.Execution = RuntimeSettings.CloneExecution(value.Execution);
        });

        var restartRequired =
            !string.Equals(previous.SkillsDirectory, value.SkillsDirectory, StringComparison.Ordinal) ||
            previous.SkillSelector != value.SkillSelector ||
            previous.Scheduler.MaxConcurrentJobs != value.Scheduler.MaxConcurrentJobs ||
            !new HashSet<string>(previous.EnabledTools, StringComparer.OrdinalIgnoreCase)
                .SetEquals(new HashSet<string>(value.EnabledTools, StringComparer.OrdinalIgnoreCase));

        return Success(restartRequired);
    }

    public async Task<ConfigWriteResult> SavePermissionsAsync(PermissionsOptions value, CancellationToken ct)
    {
        var root = await ReadRootAsync(ct).ConfigureAwait(false);

        // Cross-section guard (roadmap §3.3 payoff): the global ShellAutoAllowlist can be used
        // unattended (--unattended/--serve fall back to it when a job has no overlay of its own),
        // so it is subject to the same wildcard-requires-container rule as a per-schedule overlay
        // (see CaliperOptionsValidator.ValidateSchedules and PermissionsOptionsValidator, which
        // enforces the same rule at bind time). Read the *currently persisted* Execution section
        // fresh from the file rather than runtimeSettings — ReadRootAsync always re-reads
        // config.json, so this sees the latest saved Backend even if this call raced a concurrent
        // SaveExecutionAsync.
        var currentCaliper = ReadSection(root, CaliperSection, CaliperJsonContext.Default.CaliperOptions) ?? new CaliperOptions();
        if (UnattendedAllowlistGuard.Validate(value.ShellAutoAllowlist, currentCaliper.Execution.Backend, "The global Permissions section's") is { } wildcardError)
            return Invalid([wildcardError]);

        root[PermissionsSection] = JsonSerializer.SerializeToNode(value, CaliperJsonContext.Default.PermissionsOptions);
        await WriteRootAsync(root, ct).ConfigureAwait(false);

        // PermissionGate.EvaluateAsync reads runtimeSettings.Permissions fresh on every call, so
        // the whole section is live.
        runtimeSettings.UpdatePermissions(p =>
        {
            p.Mode = value.Mode;
            p.RememberApprovals = value.RememberApprovals;
            p.ShellAutoAllowlist = [.. value.ShellAutoAllowlist];
            p.ShellDenylist = [.. value.ShellDenylist];
            p.AutoAllowFileRoots = [.. value.AutoAllowFileRoots];
        });

        return Success(restartRequired: false);
    }

    public async Task<ConfigWriteResult> SaveProvidersAsync(ProvidersOptions value, CancellationToken ct)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(value.OpenRouter.Endpoint))
            failures.Add("Providers.OpenRouter.Endpoint must not be empty.");
        if (string.IsNullOrWhiteSpace(value.OpenRouter.AppTitle))
            failures.Add("Providers.OpenRouter.AppTitle must not be empty.");
        if (string.IsNullOrWhiteSpace(value.Gemini.Endpoint))
            failures.Add("Providers.Gemini.Endpoint must not be empty.");
        if (failures.Count > 0)
            return Invalid(failures);

        var toSerialize = new ProvidersOptions
        {
            OpenRouter = new OpenRouterOptions
            {
                Endpoint = value.OpenRouter.Endpoint,
                AppTitle = value.OpenRouter.AppTitle,
                AppReferer = value.OpenRouter.AppReferer,
                ApiKey = BlankToNull(value.OpenRouter.ApiKey),
            },
            Gemini = new GeminiOptions
            {
                Endpoint = value.Gemini.Endpoint,
                ApiKey = BlankToNull(value.Gemini.ApiKey),
            },
        };

        var root = await ReadRootAsync(ct).ConfigureAwait(false);
        var previous = ReadSection(root, ProvidersSection, CaliperJsonContext.Default.ProvidersOptions) ?? new ProvidersOptions();
        root[ProvidersSection] = JsonSerializer.SerializeToNode(toSerialize, CaliperJsonContext.Default.ProvidersOptions);
        await WriteRootAsync(root, ct).ConfigureAwait(false);

        // Provider chat clients bind IOptions<ProvidersOptions> once at singleton construction — no
        // live seam exists — but a restart is only actually needed when a bound field changed.
        // (API keys the app stores in Credential Manager aren't visible here; the caller folds those
        // in.) Don't cry wolf when e.g. only a caliper-section field was re-saved.
        var restartRequired =
            !string.Equals(previous.OpenRouter.Endpoint, toSerialize.OpenRouter.Endpoint, StringComparison.Ordinal) ||
            !string.Equals(previous.OpenRouter.AppTitle, toSerialize.OpenRouter.AppTitle, StringComparison.Ordinal) ||
            !string.Equals(previous.OpenRouter.AppReferer, toSerialize.OpenRouter.AppReferer, StringComparison.Ordinal) ||
            !string.Equals(previous.OpenRouter.ApiKey, toSerialize.OpenRouter.ApiKey, StringComparison.Ordinal) ||
            !string.Equals(previous.Gemini.Endpoint, toSerialize.Gemini.Endpoint, StringComparison.Ordinal) ||
            !string.Equals(previous.Gemini.ApiKey, toSerialize.Gemini.ApiKey, StringComparison.Ordinal);

        return Success(restartRequired);
    }

    public async Task<ConfigWriteResult> SaveMcpAsync(McpOptions value, CancellationToken ct)
    {
        var failures = new List<string>();
        var toSerialize = new McpOptions();
        foreach (var (name, server) in value.Servers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                failures.Add("Each MCP server needs a non-empty name.");
                continue;
            }

            var type = server.Type.Trim().ToLowerInvariant();
            switch (type)
            {
                case "stdio":
                    if (string.IsNullOrWhiteSpace(server.Command))
                        failures.Add($"MCP server '{name}' requires Command for stdio transport.");
                    break;
                case "http" or "streamable_http" or "streamable-http":
                    if (!Uri.TryCreate(server.Url, UriKind.Absolute, out _))
                        failures.Add($"MCP server '{name}' requires an absolute Url for HTTP transport.");
                    break;
                default:
                    failures.Add($"MCP server '{name}' has unsupported transport type '{server.Type}'.");
                    break;
            }

            toSerialize.Servers[name.Trim()] = new McpServerOptions
            {
                Type = server.Type,
                Url = server.Url,
                Command = server.Command,
                Args = [.. server.Args],
                BearerToken = BlankToNull(server.BearerToken),
                Headers = new Dictionary<string, string>(server.Headers, StringComparer.OrdinalIgnoreCase),
            };
        }

        if (failures.Count > 0)
            return Invalid(failures);

        var root = await ReadRootAsync(ct).ConfigureAwait(false);
        root[McpSection] = JsonSerializer.SerializeToNode(toSerialize, CaliperJsonContext.Default.McpOptions);
        await WriteRootAsync(root, ct).ConfigureAwait(false);

        // McpHub binds IOptions<McpOptions> once at singleton construction — reconnecting reads
        // that same snapshot, not the file, so server changes need a restart.
        return Success(restartRequired: true);
    }

    public async Task<ConfigWriteResult> SaveSearchAsync(SearchOptions value, CancellationToken ct)
    {
        // SearchOptionsValidator requires a non-blank ApiKey when Backend is Tavily. That rule
        // exists to catch a genuinely unusable configuration, but the UI may legitimately submit
        // a blank ApiKey here because the real key lives in Credential Manager (see
        // WindowsCredentialStore in Caliper.App), not in this section. Validate a copy with a
        // placeholder key so that case doesn't produce a false failure; the real (possibly blank)
        // value is what gets persisted.
        var forValidation = string.IsNullOrWhiteSpace(value.ApiKey) && string.Equals(value.Backend, "Tavily", StringComparison.OrdinalIgnoreCase)
            ? new SearchOptions
            {
                Backend = value.Backend,
                ApiKey = "placeholder",
                SearchDepth = value.SearchDepth,
                MaxResults = value.MaxResults,
                Topic = value.Topic,
            }
            : value;
        var validation = searchValidator.Validate(null, forValidation);
        if (validation.Failed)
            return Invalid(validation);

        var toSerialize = new SearchOptions
        {
            Backend = value.Backend,
            ApiKey = BlankToNull(value.ApiKey),
            SearchDepth = value.SearchDepth,
            MaxResults = value.MaxResults,
            Topic = value.Topic,
        };

        var root = await ReadRootAsync(ct).ConfigureAwait(false);
        root[SearchSection] = JsonSerializer.SerializeToNode(toSerialize, CaliperJsonContext.Default.SearchOptions);
        await WriteRootAsync(root, ct).ConfigureAwait(false);

        // ISearchBackend (Stub vs Tavily) is chosen once at DI-build time from the bound
        // IOptions<SearchOptions> snapshot — no live seam exists.
        return Success(restartRequired: true);
    }

    public async Task<ConfigWriteResult> SavePersistenceAsync(PersistenceOptions value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value.SqlitePath))
            return Invalid(["Persistence.SqlitePath must not be empty."]);

        var root = await ReadRootAsync(ct).ConfigureAwait(false);
        root[PersistenceSection] = JsonSerializer.SerializeToNode(value, CaliperJsonContext.Default.PersistenceOptions);
        await WriteRootAsync(root, ct).ConfigureAwait(false);

        // SqliteStoreBase binds IOptions<PersistenceOptions> once at singleton construction — no
        // live seam exists.
        return Success(restartRequired: true);
    }

    public async Task<ConfigWriteResult> SaveSubagentsAsync(SubagentsOptions value, CancellationToken ct)
    {
        // Reuse SaveCaliperAsync's own validation/persistence/live-update pipeline against a copy
        // of the current Caliper section with just Subagents swapped in, rather than duplicating
        // that logic here — see the interface comment for why Subagents gets its own Save method
        // even though it lives inside the Caliper section rather than being its own top-level one.
        var current = await LoadCaliperAsync(ct).ConfigureAwait(false);
        current.Subagents = value;
        return await SaveCaliperAsync(current, ct).ConfigureAwait(false);
    }

    public async Task<ConfigWriteResult> SaveSchedulesAsync(IList<ScheduleOptions> value, CancellationToken ct)
    {
        // Same shape as SaveSubagentsAsync: swap just the Schedules slice into the current Caliper
        // section and reuse SaveCaliperAsync's validate/persist/live-update pipeline. Validation
        // (unique names, cron parse, time zone, working root, model) lives once in
        // CaliperOptionsValidator.ValidateSchedules and therefore runs both here and at startup
        // binding. The whole list is live (scheduler re-reads per tick) — see the comment inside
        // SaveCaliperAsync's UpdateCaliper block.
        var current = await LoadCaliperAsync(ct).ConfigureAwait(false);
        current.Schedules = value;
        return await SaveCaliperAsync(current, ct).ConfigureAwait(false);
    }

    public async Task<ConfigWriteResult> SaveExecutionAsync(ExecutionOptions value, CancellationToken ct)
    {
        // Same nested-slice pattern as SaveSubagentsAsync/SaveSchedulesAsync: Execution lives inside
        // the Caliper section, so reuse SaveCaliperAsync's validate/persist/live-update pipeline
        // against a copy of the current Caliper section with just Execution swapped in.
        // CaliperOptionsValidator validates the *whole* CaliperOptions together, so this call also
        // re-validates every already-saved Schedules[].Permissions.ShellAutoAllowlist against the
        // new Backend (UnattendedAllowlistGuard) — flipping Backend back to Host after a schedule
        // was saved with a bare "*" allowlist under Container is rejected here, not just at
        // SaveSchedulesAsync time. The whole section is live; see the comment inside
        // SaveCaliperAsync's UpdateCaliper block for what "live" does and doesn't cover (the docker
        // probe cache in particular).
        var current = await LoadCaliperAsync(ct).ConfigureAwait(false);
        current.Execution = value;
        return await SaveCaliperAsync(current, ct).ConfigureAwait(false);
    }

    private static SubagentsOptions CloneSubagents(SubagentsOptions source)
    {
        var profiles = new Dictionary<string, SubagentProfileOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, profile) in source.Profiles)
        {
            profiles[name] = new SubagentProfileOptions
            {
                EnabledTools = [.. profile.EnabledTools],
                MaxSteps = profile.MaxSteps,
                Mode = profile.Mode,
            };
        }

        return new SubagentsOptions
        {
            MaxDepth = source.MaxDepth,
            MaxChildrenPerRun = source.MaxChildrenPerRun,
            DefaultProfile = source.DefaultProfile,
            TimeoutSeconds = source.TimeoutSeconds,
            Profiles = profiles,
        };
    }

    private async Task<JsonObject> ReadRootAsync(CancellationToken ct)
    {
        var json = await fileStore.ReadAsync(ct).ConfigureAwait(false);
        return JsonNode.Parse(json, documentOptions: s_documentOptions) as JsonObject ??
            throw new JsonException("The Caliper configuration root must be a JSON object.");
    }

    private Task WriteRootAsync(JsonObject root, CancellationToken ct) =>
        fileStore.WriteAsync(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);

    private static T? ReadSection<T>(JsonObject root, string section, JsonTypeInfo<T> typeInfo) =>
        root[section] is { } node ? JsonSerializer.Deserialize(node, typeInfo) : default;

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static ConfigWriteResult Success(bool restartRequired) =>
        new(Success: true, Error: null, RestartRequired: restartRequired);

    private static ConfigWriteResult Invalid(ValidateOptionsResult validation) =>
        Invalid(validation.Failures ?? ["Validation failed."]);

    private static ConfigWriteResult Invalid(IEnumerable<string> failures) =>
        new(Success: false, Error: string.Join(" ", failures), RestartRequired: false);
}
