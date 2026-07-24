// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Security;

namespace Caliper.App.Tests;

public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public void Chunk_codec_round_trips_large_oauth_token()
    {
        var token = string.Concat(Enumerable.Repeat("header.payload-😀.", 500));

        var chunks = WindowsCredentialStore.EncodeChunks(token);
        var decoded = WindowsCredentialStore.DecodeChunks(chunks);

        Assert.True(chunks.Count > 1);
        Assert.All(
            chunks,
            chunk => Assert.InRange(
                chunk.Length,
                1,
                WindowsCredentialStore.ChunkPayloadBytes));
        Assert.Equal(token, decoded);
    }
}
