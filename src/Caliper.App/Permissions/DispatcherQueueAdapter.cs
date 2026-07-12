// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Microsoft.UI.Dispatching;

namespace Caliper.App.Permissions;

internal sealed class DispatcherQueueAdapter(DispatcherQueue dispatcher) : IUiDispatcher
{
    public bool HasThreadAccess => dispatcher.HasThreadAccess;

    public bool TryEnqueue(Action action) =>
        dispatcher.TryEnqueue(() => action());
}
