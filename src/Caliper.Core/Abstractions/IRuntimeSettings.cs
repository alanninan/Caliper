// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;

namespace Caliper.Core.Abstractions;

public interface IRuntimeSettings
{
    CaliperOptions Caliper { get; }
    PermissionsOptions Permissions { get; }

    /// <summary>
    /// Raised after any live mutation of <see cref="Caliper"/> or <see cref="Permissions"/> so
    /// hosts can refresh views that surface runtime settings (model/provider/permission mode). May
    /// be raised on a background thread — subscribers that touch UI must marshal to their thread.
    /// </summary>
    event EventHandler? SettingsChanged;

    void SetProvider(string provider);
    void SetModel(string model);
    void SetPermissionMode(PermissionMode mode);
    bool TrySet(string key, string value, out string message);

    /// <summary>
    /// Applies <paramref name="mutate"/> to the live <see cref="Caliper"/> settings under the same
    /// lock used by the single-field setters. Used by hosts (e.g. a typed config writer) that need
    /// to push several fields live at once without one method per field.
    /// </summary>
    void UpdateCaliper(Action<CaliperOptions> mutate);

    /// <summary>
    /// Applies <paramref name="mutate"/> to the live <see cref="Permissions"/> settings under the
    /// same lock used by the single-field setters.
    /// </summary>
    void UpdatePermissions(Action<PermissionsOptions> mutate);
}
