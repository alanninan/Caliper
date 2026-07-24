// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Protocol;

namespace Caliper.Core.Security;

/// <summary>
/// Console-safe credential fallback. The file is user-local, written atomically, and restricted
/// to the current user on Unix. Windows desktop replaces this registration with Credential
/// Manager before Core is registered.
/// </summary>
internal sealed class FileProviderCredentialStore : IProviderCredentialStore
{
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(CaliperHome.Resolve(), "provider-auth.json");

    public void Save(string targetName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentNullException.ThrowIfNull(secret);

        lock (_gate)
        {
            var values = Read();
            values[targetName] = secret;
            Write(values);
        }
    }

    public bool TryRead(string targetName, out string secret)
    {
        lock (_gate)
            return Read().TryGetValue(targetName, out secret!);
    }

    public void Delete(string targetName)
    {
        lock (_gate)
        {
            var values = Read();
            if (values.Remove(targetName))
                Write(values);
        }
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize(json, CaliperJsonContext.Default.DictionaryStringString)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void Write(Dictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(values, CaliperJsonContext.Default.DictionaryStringString));
        File.Move(temporary, _path, overwrite: true);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
