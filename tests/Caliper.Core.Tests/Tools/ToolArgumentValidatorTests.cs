// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Tools;

namespace Caliper.Core.Tests.Tools;

public sealed class ToolArgumentValidatorTests
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["url"],"properties":{"url":{"type":"string","maxLength":12}}}""")
        .RootElement.Clone();

    [Fact]
    public void Validate_accepts_valid_flat_arguments()
    {
        var args = JsonDocument.Parse("""{"url":"https://a"}""").RootElement.Clone();

        Assert.Null(ToolArgumentValidator.Validate(args, s_schema));
    }

    [Fact]
    public void Validate_rejects_wrong_scalar_type()
    {
        var args = JsonDocument.Parse("""{"url":["https://a"]}""").RootElement.Clone();

        Assert.Equal("$.url must be string, got array", ToolArgumentValidator.Validate(args, s_schema));
    }

    [Fact]
    public void Validate_rejects_missing_required_property()
    {
        var args = JsonDocument.Parse("""{}""").RootElement.Clone();

        Assert.Equal("$.url is required", ToolArgumentValidator.Validate(args, s_schema));
    }

    [Fact]
    public void Validate_rejects_additional_properties()
    {
        var args = JsonDocument.Parse("""{"url":"https://a","extra":1}""").RootElement.Clone();

        Assert.Equal("$.extra is not allowed", ToolArgumentValidator.Validate(args, s_schema));
    }

    [Theory]
    [InlineData("""{"value":1}""")]
    [InlineData("""{"value":1.0}""")]
    [InlineData("""{"value":1e0}""")]
    public void Validate_compares_numeric_enum_values_numerically(string rawArgs)
    {
        var schema = JsonDocument.Parse(
            """{"type":"object","additionalProperties":false,"required":["value"],"properties":{"value":{"type":"number","enum":[1,2,3]}}}""")
            .RootElement.Clone();
        var args = JsonDocument.Parse(rawArgs).RootElement.Clone();

        Assert.Null(ToolArgumentValidator.Validate(args, schema));
    }
}
