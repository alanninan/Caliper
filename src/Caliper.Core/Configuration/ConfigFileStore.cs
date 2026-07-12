// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;

namespace Caliper.Core.Configuration;

internal sealed class ConfigFileStore : IConfigFileStore, IDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private static readonly JsonDocumentOptions s_documentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public Task<string> ReadAsync(CancellationToken ct) =>
        File.ReadAllTextAsync(CaliperHome.ConfigPath, ct);

    public async Task WriteAsync(string json, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json, s_documentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The Caliper configuration root must be a JSON object.");

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = CaliperHome.ConfigPath;
            var temporaryPath = path + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, json, ct).ConfigureAwait(false);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose() => _writeGate.Dispose();
}
