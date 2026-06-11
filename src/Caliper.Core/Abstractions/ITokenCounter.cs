// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;

namespace Caliper.Core.Abstractions;

public interface ITokenCounter
{
    int Count(string text);
    int Count(IEnumerable<ChatMessage> messages);
    void Calibrate(int estimated, int actual);
}
