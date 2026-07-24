// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Caliper.Core.Tools;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Memory;

public interface ICaliperMdProvider
{
    Task<ProjectMemoryDocument> ReadAsync(string workingRoot, CancellationToken ct);

    Task<ProjectMemoryDocument> CreateIfMissingAsync(string workingRoot, CancellationToken ct) =>
        Task.FromException<ProjectMemoryDocument>(
            new NotSupportedException("This project-memory provider cannot create files."));
}

public sealed record ProjectMemoryDocument(string Path, string Content, bool Truncated);

public sealed class CaliperMdProvider(IOptions<CaliperOptions> options) : ICaliperMdProvider
{
    private const int DefaultProjectFileCap = 4096;

    public async Task<ProjectMemoryDocument> ReadAsync(string workingRoot, CancellationToken ct)
    {
        var projectFile = options.Value.Memory.ProjectFile;
        if (string.IsNullOrWhiteSpace(projectFile))
            return new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false);

        var resolvedRoot = Path.GetFullPath(LocalPath.ResolveHome(workingRoot));
        var path = Path.Combine(resolvedRoot, projectFile);
        if (!File.Exists(path))
            return new ProjectMemoryDocument(path, string.Empty, Truncated: false);

        var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var cap = Math.Max(0, Math.Min(DefaultProjectFileCap, options.Value.ToolOutputMaxChars));
        var truncated = ToolOutput.Truncate(content, cap);
        return new ProjectMemoryDocument(path, truncated, Truncated: truncated.Length < content.Length);
    }

    public async Task<ProjectMemoryDocument> CreateIfMissingAsync(string workingRoot, CancellationToken ct)
    {
        var projectFile = options.Value.Memory.ProjectFile;
        if (string.IsNullOrWhiteSpace(projectFile))
            return new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false);

        var resolvedRoot = Path.GetFullPath(LocalPath.ResolveHome(workingRoot));
        var path = Path.GetFullPath(Path.Combine(resolvedRoot, projectFile));
        var relative = Path.GetRelativePath(resolvedRoot, path);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The project memory file must stay inside the working folder.");

        if (!File.Exists(path))
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "# Project memory\n", ct).ConfigureAwait(false);
        }

        return await ReadAsync(workingRoot, ct).ConfigureAwait(false);
    }
}
