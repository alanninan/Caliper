// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Microsoft.Extensions.Options;

namespace Caliper.Core.Configuration;

internal sealed class SearchOptionsValidator : IValidateOptions<SearchOptions>
{
    public ValidateOptionsResult Validate(string? name, SearchOptions options)
    {
        var failures = new List<string>();

        if (string.Equals(options.Backend, "Tavily", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add($"{nameof(SearchOptions.ApiKey)} must be set when Search:Backend is Tavily.");
        }

        if (options.MaxResults is < 0 or > 20)
            failures.Add($"{nameof(SearchOptions.MaxResults)} must be between 0 and 20.");

        if (!string.Equals(options.SearchDepth, "basic", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.SearchDepth, "advanced", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{nameof(SearchOptions.SearchDepth)} must be 'basic' or 'advanced'.");
        }

        if (!string.Equals(options.Topic, "general", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Topic, "news", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Topic, "finance", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{nameof(SearchOptions.Topic)} must be 'general', 'news', or 'finance'.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
