// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Agents;

internal static class ListExtensions
{
    /// <summary>
    /// Returns how many trailing elements of <paramref name="list"/> equal <paramref name="value"/>.
    /// </summary>
    public static int TrailingRepeatCount<T>(this IList<T> list, T value) where T : IEquatable<T>
    {
        var count = 0;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Equals(value)) count++;
            else break;
        }
        return count;
    }
}
