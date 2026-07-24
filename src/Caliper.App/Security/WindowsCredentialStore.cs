// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Caliper.Core.Abstractions;

[assembly: DisableRuntimeMarshalling]

namespace Caliper.App.Security;

public interface ICredentialStore : IProviderCredentialStore;

/// <summary>
/// Stores provider/search API keys and MCP bearer tokens in the Windows Credential Manager
/// instead of Caliper.Core's config.json. Caliper.App is unpackaged (no package identity), so
/// the WinRT PasswordVault API is unavailable here - CredWrite/CredRead is the packaging-agnostic
/// equivalent. The App registers this implementation as Core's provider credential store so
/// credentials can be changed without placing them in config.json.
/// </summary>
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int MaxCredentialBlobBytes = 5 * 512;
    internal const int ChunkPayloadBytes = 2400;
    private const string ChunkManifestPrefix = "CaliperCredentialChunks:v1:";
    private readonly object _gate = new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref CREDENTIALW credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string targetName, uint type, uint flags);

    public void Save(string targetName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentNullException.ThrowIfNull(secret);

        lock (_gate)
            SaveCore(targetName, secret);
    }

    private static void SaveCore(string targetName, string secret)
    {
        var secretBytes = Encoding.Unicode.GetBytes(secret);
        TryReadManifest(targetName, out var previousManifest);
        if (secretBytes.Length <= MaxCredentialBlobBytes)
        {
            WriteRaw(targetName, secretBytes);
            DeleteChunks(targetName, previousManifest);
            return;
        }

        var chunkId = Guid.NewGuid().ToString("N");
        var chunks = EncodeChunks(secret);
        var manifest = new CredentialChunkManifest(chunkId, chunks.Count);
        var writtenChunks = 0;
        try
        {
            for (var index = 0; index < chunks.Count; index++)
            {
                WriteRaw(ChunkTarget(targetName, chunkId, index), chunks[index]);
                writtenChunks++;
            }

            WriteRaw(targetName, Encoding.Unicode.GetBytes(FormatManifest(manifest)));
        }
        catch
        {
            for (var index = 0; index < writtenChunks; index++)
                DeleteRaw(ChunkTarget(targetName, chunkId, index));
            throw;
        }

        DeleteChunks(targetName, previousManifest);
    }

    private static void WriteRaw(string targetName, byte[] secretBytes)
    {
        if (secretBytes.Length > MaxCredentialBlobBytes)
            throw new InvalidOperationException(
                $"Credential chunk for '{targetName}' exceeds {MaxCredentialBlobBytes} bytes.");

        var targetNamePtr = Marshal.StringToHGlobalUni(targetName);
        var userNamePtr = Marshal.StringToHGlobalUni("Caliper");
        var blobPtr = secretBytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            if (blobPtr != IntPtr.Zero)
                Marshal.Copy(secretBytes, 0, blobPtr, secretBytes.Length);

            var credential = new CREDENTIALW
            {
                Type = CredTypeGeneric,
                TargetName = targetNamePtr,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = userNamePtr,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException(
                    $"Failed to save credential '{targetName}' (Win32 error {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(targetNamePtr);
            Marshal.FreeHGlobal(userNamePtr);
            if (blobPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(blobPtr);
        }
    }

    public unsafe bool TryRead(string targetName, out string secret)
    {
        lock (_gate)
            return TryReadCore(targetName, out secret);
    }

    private static bool TryReadCore(string targetName, out string secret)
    {
        secret = string.Empty;
        if (!TryReadRaw(targetName, out var bytes))
            return false;

        var storedValue = Encoding.Unicode.GetString(bytes);
        if (!TryParseManifest(storedValue, out var manifest))
        {
            secret = storedValue;
            return true;
        }

        var chunks = new List<byte[]>(manifest.Count);
        for (var index = 0; index < manifest.Count; index++)
        {
            if (!TryReadRaw(ChunkTarget(targetName, manifest.Id, index), out var chunk))
                return false;
            chunks.Add(chunk);
        }

        secret = DecodeChunks(chunks);
        return true;
    }

    private static unsafe bool TryReadRaw(string targetName, out byte[] bytes)
    {
        bytes = [];
        if (!CredRead(targetName, CredTypeGeneric, 0, out var credentialPtr))
            return false;

        try
        {
            var credential = *(CREDENTIALW*)credentialPtr;
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                return false;

            bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return true;
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public void Delete(string targetName)
    {
        lock (_gate)
        {
            TryReadManifest(targetName, out var manifest);
            DeleteRaw(targetName);
            DeleteChunks(targetName, manifest);
        }
    }

    internal static IReadOnlyList<byte[]> EncodeChunks(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var chunks = new List<byte[]>((bytes.Length + ChunkPayloadBytes - 1) / ChunkPayloadBytes);
        for (var offset = 0; offset < bytes.Length; offset += ChunkPayloadBytes)
        {
            var length = Math.Min(ChunkPayloadBytes, bytes.Length - offset);
            chunks.Add(bytes.AsSpan(offset, length).ToArray());
        }

        return chunks;
    }

    internal static string DecodeChunks(IReadOnlyList<byte[]> chunks)
    {
        var length = chunks.Sum(static chunk => chunk.Length);
        var bytes = new byte[length];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(bytes, offset);
            offset += chunk.Length;
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static void TryReadManifest(string targetName, out CredentialChunkManifest? manifest)
    {
        manifest = null;
        if (!TryReadRaw(targetName, out var bytes))
            return;

        if (TryParseManifest(Encoding.Unicode.GetString(bytes), out var parsed))
            manifest = parsed;
    }

    private static bool TryParseManifest(string value, out CredentialChunkManifest manifest)
    {
        manifest = null!;
        if (!value.StartsWith(ChunkManifestPrefix, StringComparison.Ordinal))
            return false;

        var parts = value[ChunkManifestPrefix.Length..].Split(':', 2);
        if (parts.Length != 2 ||
            !Guid.TryParseExact(parts[0], "N", out _) ||
            !int.TryParse(parts[1], out var count) ||
            count is <= 0 or > 1024)
        {
            return false;
        }

        manifest = new CredentialChunkManifest(parts[0], count);
        return true;
    }

    private static string FormatManifest(CredentialChunkManifest manifest) =>
        $"{ChunkManifestPrefix}{manifest.Id}:{manifest.Count}";

    private static string ChunkTarget(string targetName, string id, int index) =>
        $"{targetName}/Chunks/{id}/{index:D4}";

    private static void DeleteChunks(string targetName, CredentialChunkManifest? manifest)
    {
        if (manifest is null)
            return;

        for (var index = 0; index < manifest.Count; index++)
            DeleteRaw(ChunkTarget(targetName, manifest.Id, index));
    }

    private static void DeleteRaw(string targetName) =>
        CredDelete(targetName, CredTypeGeneric, 0);

    private sealed record CredentialChunkManifest(string Id, int Count);
}

public static class CredentialTargets
{
    public const string OpenRouterApiKey = ProviderCredentialTargets.OpenRouterApiKey;
    public const string GeminiApiKey = ProviderCredentialTargets.GeminiApiKey;
    public const string OpenAIApiKey = ProviderCredentialTargets.OpenAIApiKey;
    public const string OpenAICodexAccessToken = ProviderCredentialTargets.OpenAICodexAccessToken;
    public const string OpenAICodexRefreshToken = ProviderCredentialTargets.OpenAICodexRefreshToken;
    public const string SearchApiKey = "Caliper/Search/ApiKey";

    public static string McpBearerToken(string serverName) => $"Caliper/Mcp/{serverName}/BearerToken";
}
