// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Microsoft.Extensions.Options;

namespace Caliper.Core.Configuration;

/// <summary>
/// Cross-section validation for the top-level <c>Permissions</c> config section (roadmap §3.3):
/// the global <c>ShellAutoAllowlist</c> can be used unattended too (the console's
/// <c>--unattended</c>/<c>--serve</c> paths fall back to it whenever a job has no
/// <c>Permissions</c> overlay of its own), so it is guarded by the same wildcard-requires-container
/// rule as a per-schedule overlay (<see cref="CaliperOptionsValidator"/>'s
/// <c>ValidateSchedules</c>). <see cref="PermissionsOptions"/> and <see cref="CaliperOptions"/> bind
/// from independent config sections and validate independently, so this reads a live
/// <see cref="IOptions{TOptions}"/> snapshot of <see cref="CaliperOptions"/> to see
/// <see cref="ExecutionOptions.Backend"/> rather than requiring the two sections to be validated
/// together.
/// </summary>
internal sealed class PermissionsOptionsValidator(IOptions<CaliperOptions> caliperOptions) : IValidateOptions<PermissionsOptions>
{
    public ValidateOptionsResult Validate(string? name, PermissionsOptions options)
    {
        var backend = caliperOptions.Value.Execution.Backend;
        var error = UnattendedAllowlistGuard.Validate(options.ShellAutoAllowlist, backend, "The global Permissions section's");
        return error is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
    }
}
