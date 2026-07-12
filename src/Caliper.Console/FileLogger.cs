// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Caliper.Console;

/// <summary>
/// A minimal, AOT-safe file logger. Caliper.Core signals degraded states (respond-only fallback,
/// tokenizer fallback, MCP errors, summarization fallback) only through <see cref="ILogger"/>, so
/// the console host writes Warning+ to a file the user can inspect instead of discarding them.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();

    public FileLoggerProvider(string filePath, LogLevel minLevel)
    {
        _filePath = filePath;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose()
    {
    }

    private bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel && logLevel != LogLevel.None;

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                File.AppendAllText(_filePath, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take down the app; drop the line if the file is unwritable.
            }
        }
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!provider.IsEnabled(logLevel))
                return;

            var builder = new StringBuilder()
                .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Append(" [").Append(logLevel).Append("] ")
                .Append(category).Append(" - ")
                .Append(formatter(state, exception));

            if (exception is not null)
                builder.Append(Environment.NewLine).Append(exception);

            builder.Append(Environment.NewLine);
            provider.Write(builder.ToString());
        }
    }
}
