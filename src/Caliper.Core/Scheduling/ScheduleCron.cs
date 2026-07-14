// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics.CodeAnalysis;
using Cronos;

namespace Caliper.Core.Scheduling;

/// <summary>
/// Shared cron/time-zone plumbing for the roadmap §3.2b scheduler: one parse/resolve path used by
/// <c>CaliperOptionsValidator</c> (and therefore <c>ConfigWriter.SaveSchedulesAsync</c>), by
/// <see cref="SchedulerHostedService"/>'s tick loop, and by the console's <c>/schedule list</c>
/// on-demand next-occurrence display — so "valid at save" and "valid at run" can never drift.
/// </summary>
public static class ScheduleCron
{
    /// <summary>The <c>ScheduleOptions.TimeZone</c> sentinel meaning the machine's local zone.</summary>
    public const string LocalTimeZone = "local";

    public static bool TryParseCron(
        string? cron,
        [NotNullWhen(true)] out CronExpression? expression,
        [NotNullWhen(false)] out string? error)
    {
        expression = null;
        if (string.IsNullOrWhiteSpace(cron))
        {
            error = "cron expression is empty";
            return false;
        }

        try
        {
            expression = CronExpression.Parse(cron);
            error = null;
            return true;
        }
        catch (CronFormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryResolveTimeZone(
        string? timeZone,
        [NotNullWhen(true)] out TimeZoneInfo? zone,
        [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(timeZone) ||
            string.Equals(timeZone.Trim(), LocalTimeZone, StringComparison.OrdinalIgnoreCase))
        {
            zone = TimeZoneInfo.Local;
            error = null;
            return true;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone.Trim());
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Computes the job's next occurrence strictly after <paramref name="from"/>, or null when the
    /// cron/time zone is invalid (config edited by hand under a running scheduler) or the
    /// expression can never fire again (e.g. <c>0 0 30 2 *</c> — Cronos returns null). Callers
    /// that need to distinguish the two use <paramref name="error"/>: non-null means invalid
    /// config, null means a valid expression with no future occurrence.
    /// </summary>
    public static DateTimeOffset? GetNextOccurrence(
        Configuration.ScheduleOptions job,
        DateTimeOffset from,
        out string? error)
    {
        if (!TryParseCron(job.Cron, out var expression, out var cronError))
        {
            error = $"invalid cron '{job.Cron}': {cronError}";
            return null;
        }

        if (!TryResolveTimeZone(job.TimeZone, out var zone, out var zoneError))
        {
            error = $"unresolvable time zone '{job.TimeZone}': {zoneError}";
            return null;
        }

        error = null;
        // Cronos handles DST itself: spring-forward occurrences shift, and interval expressions
        // legitimately fire twice in the fall-back repeated hour (accepted — roadmap §3.2b; the
        // per-job overlap guard serializes the job either way).
        return expression.GetNextOccurrence(from, zone);
    }
}
