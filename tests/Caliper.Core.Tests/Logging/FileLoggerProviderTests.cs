// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Globalization;
using Caliper.Core.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Caliper.Core.Tests.Logging;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "caliper-filelogger-" + Guid.NewGuid().ToString("N"));

    public FileLoggerProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Log_at_or_above_min_level_writes_timestamp_level_category_and_message()
    {
        var path = Path.Combine(_root, "caliper.log");
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 7, 16, 12, 30, 0, TimeSpan.Zero));
        using var provider = new FileLoggerProvider(path, LogLevel.Warning, timeProvider);
        var logger = provider.CreateLogger("Caliper.Test.Category");

        logger.LogWarning("something degraded: {Reason}", "fallback");

        var content = File.ReadAllText(path);
        Assert.Contains(timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), content, StringComparison.Ordinal);
        Assert.Contains("[Warning]", content, StringComparison.Ordinal);
        Assert.Contains("Caliper.Test.Category", content, StringComparison.Ordinal);
        Assert.Contains("something degraded: fallback", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_below_min_level_is_not_written()
    {
        var path = Path.Combine(_root, "caliper.log");
        using var provider = new FileLoggerProvider(path, LogLevel.Warning, new FakeTimeProvider());
        var logger = provider.CreateLogger("Caliper.Test.Category");

        logger.LogInformation("this should be dropped");

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Log_with_exception_appends_exception_on_its_own_line()
    {
        var path = Path.Combine(_root, "caliper.log");
        using var provider = new FileLoggerProvider(path, LogLevel.Warning, new FakeTimeProvider());
        var logger = provider.CreateLogger("Caliper.Test.Category");
        var exception = new InvalidOperationException("boom");

        logger.LogError(exception, "unexpected failure");

        var lines = File.ReadAllText(path).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.Contains("unexpected failure", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(exception.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void Log_to_unwritable_path_does_not_throw()
    {
        // _root is a directory, not a file: AppendAllText against it fails, and the provider must
        // swallow the failure rather than let logging take down the caller.
        using var provider = new FileLoggerProvider(_root, LogLevel.Warning, new FakeTimeProvider());
        var logger = provider.CreateLogger("Caliper.Test.Category");

        var exception = Record.Exception(() => logger.LogWarning("this write cannot succeed"));

        Assert.Null(exception);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
