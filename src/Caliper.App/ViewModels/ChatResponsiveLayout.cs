// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
namespace Caliper.App.ViewModels;

public enum ChatLayoutBand
{
    Compact,
    Medium,
    Wide,
}

public readonly record struct ChatResponsiveState(
    ChatLayoutBand Band,
    bool SessionsInline,
    bool InspectorInline,
    bool OverlaysMutuallyExclusive);

public static class ChatResponsiveLayout
{
    public static ChatResponsiveState Resolve(double width) =>
        width >= 1360
            ? new(ChatLayoutBand.Wide, SessionsInline: true, InspectorInline: true, OverlaysMutuallyExclusive: false)
            : width >= 1008
                ? new(ChatLayoutBand.Medium, SessionsInline: false, InspectorInline: true, OverlaysMutuallyExclusive: false)
                : new(ChatLayoutBand.Compact, SessionsInline: false, InspectorInline: false, OverlaysMutuallyExclusive: true);
}
