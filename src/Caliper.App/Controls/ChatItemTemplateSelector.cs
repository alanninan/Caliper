// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Caliper.App.Controls;

public sealed class ChatItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AssistantTemplate { get; set; }
    public DataTemplate? ReasoningTemplate { get; set; }
    public DataTemplate? ToolTemplate { get; set; }
    public DataTemplate? ToolActivityTemplate { get; set; }
    public DataTemplate? ApprovalTemplate { get; set; }
    public DataTemplate? StatusTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item switch
        {
            UserMessageViewModel => UserTemplate,
            AssistantMessageViewModel => AssistantTemplate,
            ReasoningViewModel => ReasoningTemplate,
            ToolActivityViewModel => ToolActivityTemplate,
            ToolCallViewModel => ToolTemplate,
            ApprovalViewModel => ApprovalTemplate,
            RunStatusViewModel => StatusTemplate,
            _ => base.SelectTemplateCore(item),
        };
}
