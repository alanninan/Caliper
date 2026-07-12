// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.RegularExpressions;

namespace Caliper.Core.Tools.BuiltIn;

internal static class GlobMatcher
{
    /// <summary>
    /// Compiles a glob pattern to a full-match regex. Paths are compared with forward slashes.
    /// <c>**/</c> matches zero or more directory segments (so <c>**/*.cs</c> matches a file at the
    /// root as well as nested), <c>**</c> matches across separators, <c>*</c> matches within a
    /// single segment, and <c>?</c> matches one character.
    /// </summary>
    public static Regex ToRegex(string pattern, bool ignoreCase = true)
    {
        var escaped = Regex.Escape(pattern.Replace('\\', '/'))
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal);

        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex("^" + escaped + "$", options, TimeSpan.FromSeconds(2));
    }
}
