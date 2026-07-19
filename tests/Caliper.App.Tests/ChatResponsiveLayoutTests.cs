// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
using Caliper.App.ViewModels;

namespace Caliper.App.Tests;

public sealed class ChatResponsiveLayoutTests
{
    [Theory]
    [InlineData(800, ChatLayoutBand.Compact, false, false, true)]
    [InlineData(1008, ChatLayoutBand.Medium, false, true, false)]
    [InlineData(1359, ChatLayoutBand.Medium, false, true, false)]
    [InlineData(1360, ChatLayoutBand.Wide, true, true, false)]
    public void Resolve_width_returns_expected_layout(
        double width,
        ChatLayoutBand expectedBand,
        bool sessionsInline,
        bool inspectorInline,
        bool mutuallyExclusive)
    {
        var result = ChatResponsiveLayout.Resolve(width);

        Assert.Equal(expectedBand, result.Band);
        Assert.Equal(sessionsInline, result.SessionsInline);
        Assert.Equal(inspectorInline, result.InspectorInline);
        Assert.Equal(mutuallyExclusive, result.OverlaysMutuallyExclusive);
    }
}
