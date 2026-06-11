// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;

namespace Caliper.Core.Abstractions;

public interface IRuntimeSettings
{
    CaliperOptions Caliper { get; }
    PermissionsOptions Permissions { get; }

    void SetModel(string model);
    void SetPermissionMode(PermissionMode mode);
    bool TrySet(string key, string value, out string message);
}
