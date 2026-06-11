// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Extensions.Logging;

namespace Caliper.Core.Agents;

public sealed class TurnStrategySelector(
    NativeToolStrategy native,
    ConstrainedEnvelopeStrategy constrained,
    IRuntimeSettings runtimeSettings,
    IModelCapabilityProvider capabilityProvider,
    ILogger<TurnStrategySelector> logger) : ITurnStrategy
{
    public async IAsyncEnumerable<TurnUpdate> NextAsync(
        TurnContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var options = runtimeSettings.Caliper;
        var capabilities = await capabilityProvider.GetAsync(options.Model, ct).ConfigureAwait(false);
        var selected = options.TurnStrategy switch
        {
            TurnStrategyKind.Native => capabilities.SupportsTools
                ? native
                : throw new InvalidOperationException($"Model '{options.Model}' does not advertise native tool support required by TurnStrategy=Native."),

            TurnStrategyKind.Constrained or TurnStrategyKind.SingleEnvelope or TurnStrategyKind.TwoPhase => capabilities.SupportsStructuredOutputs
                ? constrained
                : throw new InvalidOperationException($"Model '{options.Model}' does not advertise structured output support required by TurnStrategy={options.TurnStrategy}."),

            TurnStrategyKind.Auto => SelectAuto(context, capabilities.SupportsTools, capabilities.SupportsStructuredOutputs, options.Model),

            _ => throw new InvalidOperationException($"Unsupported turn strategy: {options.TurnStrategy}."),
        };

        await foreach (var update in selected.NextAsync(context, ct).ConfigureAwait(false))
            yield return update;
    }

    private ITurnStrategy SelectAuto(
        TurnContext context,
        bool supportsTools,
        bool supportsStructuredOutputs,
        string model)
    {
        if (context.Tools.Enabled.Count > 0 && supportsTools)
            return native;

        if (supportsStructuredOutputs)
            return constrained;

        if (context.Tools.Enabled.Count > 0)
        {
            logger.LogWarning(
                "Model '{Model}' supports neither native tools nor structured outputs; continuing in respond-only mode.",
                model);
        }

        return native;
    }
}
