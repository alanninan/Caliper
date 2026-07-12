// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Security;

namespace Caliper.App.Tests;

/// <summary>
/// In-memory stand-in for WindowsCredentialStore so settings view-model tests never touch the
/// real Windows Credential Manager on the machine running them.
/// </summary>
internal sealed class FakeCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public void Save(string targetName, string secret) => _values[targetName] = secret;

    public bool TryRead(string targetName, out string secret) => _values.TryGetValue(targetName, out secret!);

    public void Delete(string targetName) => _values.Remove(targetName);
}
