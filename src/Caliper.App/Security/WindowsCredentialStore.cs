// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

[assembly: DisableRuntimeMarshalling]

namespace Caliper.App.Security;

public interface ICredentialStore
{
    void Save(string targetName, string secret);
    bool TryRead(string targetName, out string secret);
    void Delete(string targetName);
}

/// <summary>
/// Stores provider/search API keys and MCP bearer tokens in the Windows Credential Manager
/// instead of Caliper.Core's config.json. Caliper.App is unpackaged (no package identity), so
/// the WinRT PasswordVault API is unavailable here - CredWrite/CredRead is the packaging-agnostic
/// equivalent. Core has no concept of this store; the App layers resolved secrets into
/// IConfiguration at startup so Core keeps treating them as ordinary bound option values.
/// </summary>
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

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
        var secretBytes = Encoding.Unicode.GetBytes(secret);
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
        secret = string.Empty;
        if (!CredRead(targetName, CredTypeGeneric, 0, out var credentialPtr))
            return false;

        try
        {
            var credential = *(CREDENTIALW*)credentialPtr;
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                return false;

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            secret = Encoding.Unicode.GetString(bytes);
            return true;
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public void Delete(string targetName) => CredDelete(targetName, CredTypeGeneric, 0);
}

public static class CredentialTargets
{
    public const string OpenRouterApiKey = "Caliper/Providers/OpenRouter/ApiKey";
    public const string GeminiApiKey = "Caliper/Providers/Gemini/ApiKey";
    public const string SearchApiKey = "Caliper/Search/ApiKey";

    public static string McpBearerToken(string serverName) => $"Caliper/Mcp/{serverName}/BearerToken";
}
