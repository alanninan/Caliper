// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Events;
using Caliper.Core.Permissions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Caliper.Console.Rendering;

public sealed class ToolPanelRenderer
{
    private const int MaxPayloadChars = 360;

    public static Panel ToolInvoked(ToolInvoked evt) =>
        PanelFor("tool", evt.Tool, Truncate(evt.Arguments.GetRawText()), Color.Yellow);

    public static Panel ToolSucceeded(ToolSucceeded evt) =>
        PanelFor("tool ok", evt.Tool, Truncate(evt.Output), Color.Green);

    public static Panel ToolFailed(ToolFailed evt) =>
        PanelFor("tool failed", evt.Tool, Truncate(evt.Error), Color.Red);

    public static Panel PermissionRequested(PermissionRequest request)
    {
        var text = $"[bold]{Markup.Escape(request.Tool)}[/] [dim]({request.Effect})[/]\n{ArgumentsSummary(request.Arguments)}";
        if (!string.IsNullOrWhiteSpace(request.Reason))
            text += $"\n[dim]{Markup.Escape(request.Reason)}[/]";

        return new Panel(new Markup(text))
            .Header("permission")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Yellow);
    }

    public static Panel PermissionResolved(PermissionResolved evt)
    {
        var color = evt.Decision == PermissionDecision.Deny ? Color.Red : Color.Green;
        return PanelFor("permission", evt.Tool, evt.Decision.ToString(), color);
    }

    private static Panel PanelFor(string header, string name, string body, Color color) =>
        new Panel(new Markup($"[bold]{Markup.Escape(name)}[/]\n[dim]{Markup.Escape(body)}[/]"))
            .Header(header)
            .Border(BoxBorder.Rounded)
            .BorderColor(color);

    private static string ArgumentsSummary(JsonElement arguments) =>
        Markup.Escape(Truncate(arguments.GetRawText()));

    private static string Truncate(string text)
    {
        if (text.Length <= MaxPayloadChars)
            return text;

        // Don't cut through a surrogate pair, which would render as a broken glyph.
        var end = MaxPayloadChars;
        if (char.IsHighSurrogate(text[end - 1]))
            end--;
        return text[..end] + "...";
    }
}
