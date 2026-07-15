// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Configuration;
using Caliper.Core.Protocol;

namespace Caliper.Core.Tests.Configuration;

/// <summary>
/// A8: <see cref="PermissionMode"/>, <see cref="ExecutionBackendKind"/>,
/// <see cref="ExecutionNetworkKind"/>, <see cref="TurnStrategyKind"/>, and
/// <see cref="SkillSelectorKind"/> now carry <c>[JsonConverter(typeof(JsonStringEnumConverter&lt;T&gt;))]</c>
/// so <see cref="ConfigWriter"/> writes hand-editable string values into <c>config.json</c> instead
/// of the underlying int. <c>JsonStringEnumConverter&lt;T&gt;</c> still reads a plain integer on
/// deserialize (that's the built-in default, not something this codebase configured away), so a
/// pre-existing <c>config.json</c> written by an older build — before this change — keeps loading
/// without a migration step. These tests exercise the source-gen context directly
/// (<see cref="CaliperJsonContext"/>), one layer below <see cref="ConfigWriter"/>'s own round-trip
/// tests in <c>ConfigWriterTests</c>.
/// </summary>
public sealed class CaliperOptionsEnumSerializationTests
{
    [Fact]
    public void PermissionsOptions_deserializes_a_legacy_int_valued_Mode()
    {
        const string json = """{"Mode": 1, "RememberApprovals": true}""";

        var options = JsonSerializer.Deserialize(json, CaliperJsonContext.Default.PermissionsOptions);

        Assert.NotNull(options);
        Assert.Equal(PermissionMode.Auto, options!.Mode); // Auto == 1 in declaration order
    }

    [Fact]
    public void CaliperOptions_deserializes_legacy_int_valued_TurnStrategy_and_SkillSelector()
    {
        const string json = """{"TurnStrategy": 1, "SkillSelector": 1}""";

        var options = JsonSerializer.Deserialize(json, CaliperJsonContext.Default.CaliperOptions);

        Assert.NotNull(options);
        Assert.Equal(TurnStrategyKind.Native, options!.TurnStrategy); // Native == 1
        Assert.Equal(SkillSelectorKind.Keyword, options.SkillSelector); // Keyword == 1
    }

    [Fact]
    public void ExecutionOptions_deserializes_legacy_int_valued_Backend_and_Network()
    {
        const string json = """{"Backend": 1, "Network": 1}""";

        var options = JsonSerializer.Deserialize(json, CaliperJsonContext.Default.ExecutionOptions);

        Assert.NotNull(options);
        Assert.Equal(ExecutionBackendKind.Container, options!.Backend); // Container == 1
        Assert.Equal(ExecutionNetworkKind.Bridge, options.Network); // Bridge == 1
    }

    [Fact]
    public void PermissionsOptions_serializes_Mode_as_a_string()
    {
        var json = JsonSerializer.Serialize(
            new PermissionsOptions { Mode = PermissionMode.Auto },
            CaliperJsonContext.Default.PermissionsOptions);

        using var document = JsonDocument.Parse(json);
        var mode = document.RootElement.GetProperty("Mode");
        Assert.Equal(JsonValueKind.String, mode.ValueKind);
        Assert.Equal("Auto", mode.GetString());
    }

    [Fact]
    public void CaliperOptions_serializes_TurnStrategy_and_SkillSelector_as_strings()
    {
        var json = JsonSerializer.Serialize(
            new CaliperOptions { TurnStrategy = TurnStrategyKind.Native, SkillSelector = SkillSelectorKind.Keyword },
            CaliperJsonContext.Default.CaliperOptions);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("Native", document.RootElement.GetProperty("TurnStrategy").GetString());
        Assert.Equal("Keyword", document.RootElement.GetProperty("SkillSelector").GetString());
    }

    [Fact]
    public void ExecutionOptions_serializes_Backend_and_Network_as_strings()
    {
        var json = JsonSerializer.Serialize(
            new ExecutionOptions { Backend = ExecutionBackendKind.Container, Network = ExecutionNetworkKind.Bridge },
            CaliperJsonContext.Default.ExecutionOptions);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("Container", document.RootElement.GetProperty("Backend").GetString());
        Assert.Equal("Bridge", document.RootElement.GetProperty("Network").GetString());
    }
}
