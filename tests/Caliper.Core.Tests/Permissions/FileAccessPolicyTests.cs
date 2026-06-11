// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Configuration;
using Caliper.Core.Permissions;

namespace Caliper.Core.Tests.Permissions;

public sealed class FileAccessPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "caliper-policy-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "caliper-policy-outside-" + Guid.NewGuid().ToString("N"));

    public FileAccessPolicyTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    [Fact]
    public void Requires_permission_for_symlink_escape_under_working_root()
    {
        var link = Path.Combine(_root, "linked-outside");
        try
        {
            Directory.CreateSymbolicLink(link, _outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var policy = new FileAccessPolicy(
            new CaliperOptions { WorkingRoot = _root },
            new PermissionsOptions());

        var requires = policy.RequiresPermission(
            "read_file",
            JsonSerializer.SerializeToElement(new { path = Path.Combine(link, "secret.txt") }));

        Assert.True(requires);
    }

    [Fact]
    public void Auto_allow_root_can_authorize_resolved_symlink_target()
    {
        var link = Path.Combine(_root, "allowed-link");
        try
        {
            Directory.CreateSymbolicLink(link, _outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var policy = new FileAccessPolicy(
            new CaliperOptions { WorkingRoot = _root },
            new PermissionsOptions { AutoAllowFileRoots = [_outside] });

        var requires = policy.RequiresPermission(
            "write_file",
            JsonSerializer.SerializeToElement(new { path = Path.Combine(link, "out.txt") }));

        Assert.False(requires);
    }

    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_outside);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
