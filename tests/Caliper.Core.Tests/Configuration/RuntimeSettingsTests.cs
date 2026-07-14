// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Globalization;
using Caliper.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Configuration;

public sealed class RuntimeSettingsTests
{
    private static RuntimeSettings Build(CaliperOptions? caliper = null, PermissionsOptions? permissions = null) =>
        new(Options.Create(caliper ?? new CaliperOptions()), Options.Create(permissions ?? new PermissionsOptions()));

    [Fact]
    public void SetModel_updates_live_caliper_snapshot()
    {
        var settings = Build();

        settings.SetModel("new-model");

        Assert.Equal("new-model", settings.Caliper.Model);
    }

    [Fact]
    public void Caliper_getter_returns_defensive_clone()
    {
        var settings = Build();

        settings.Caliper.EnabledTools.Add("mutated-only-locally");

        Assert.DoesNotContain("mutated-only-locally", settings.Caliper.EnabledTools);
    }

    [Fact]
    public void UpdateCaliper_applies_mutation_to_live_settings()
    {
        var settings = Build();

        settings.UpdateCaliper(c =>
        {
            c.MaxSteps = 99;
            c.Context.KeepRecentTurns = 3;
        });

        Assert.Equal(99, settings.Caliper.MaxSteps);
        Assert.Equal(3, settings.Caliper.Context.KeepRecentTurns);
    }

    [Fact]
    public void UpdatePermissions_applies_mutation_to_live_settings()
    {
        var settings = Build();

        settings.UpdatePermissions(p =>
        {
            p.RememberApprovals = false;
            p.ShellAutoAllowlist.Add("git log");
        });

        Assert.False(settings.Permissions.RememberApprovals);
        Assert.Contains("git log", settings.Permissions.ShellAutoAllowlist);
    }

    [Theory]
    [InlineData("temperature", "0.7")]
    [InlineData("context.compactAtFraction", "0.5")]
    public void TrySet_parses_decimals_invariantly_under_comma_decimal_culture(string key, string value)
    {
        var settings = Build();
        var original = CultureInfo.CurrentCulture;
        try
        {
            // On a de-DE machine "0.7" must still parse as seven-tenths (comma is the decimal
            // separator there), not fail or be misread.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var ok = settings.TrySet(key, value, out var message);
            Assert.True(ok, message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal(
            double.Parse(value, CultureInfo.InvariantCulture),
            key == "temperature" ? settings.Caliper.Temperature : settings.Caliper.Context.CompactAtFraction);
    }

    [Fact]
    public void Caliper_getter_deep_clones_Subagents_profiles()
    {
        var settings = Build();

        settings.Caliper.Subagents.Profiles["research"].EnabledTools.Add("mutated-only-locally");

        Assert.DoesNotContain("mutated-only-locally", settings.Caliper.Subagents.Profiles["research"].EnabledTools);
    }

    [Fact]
    public void UpdateCaliper_applies_Subagents_mutation_to_live_settings()
    {
        var settings = Build();

        settings.UpdateCaliper(c => c.Subagents.MaxDepth = 5);

        Assert.Equal(5, settings.Caliper.Subagents.MaxDepth);
    }

    [Fact]
    public void SetModel_raises_SettingsChanged()
    {
        var settings = Build();
        var raised = 0;
        settings.SettingsChanged += (_, _) => raised++;

        settings.SetModel("m");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void TrySet_raises_SettingsChanged_only_on_success()
    {
        var settings = Build();
        var raised = 0;
        settings.SettingsChanged += (_, _) => raised++;

        Assert.True(settings.TrySet("temperature", "0.4", out _));
        Assert.False(settings.TrySet("temperature", "not-a-number", out _));

        Assert.Equal(1, raised);
    }
}
