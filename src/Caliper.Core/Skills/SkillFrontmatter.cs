// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Skills;

internal sealed class SkillFrontmatter
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? License { get; set; }
    public string? Compatibility { get; set; }

    public string? AllowedTools { get; set; }
}
