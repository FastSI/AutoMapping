using Microsoft.Extensions.Logging;
using AutoMapping.Reporting;

namespace AutoMapping.Alerting;

/// <summary>
/// Tracks which conditions are active and decides when to tell the sinks: once when a condition starts, again on escalation to
/// Critical, as a reminder after the cooldown, and once when it has been gone for <see cref="AlertOptions.ClearEvaluations"/> checks.
/// A failing sink is logged and retried, never thrown: alerting cannot break the host.
/// </summary>
public sealed class AlertManager
{
    private sealed class Active
    {
        public AlertCondition Condition = null!;
        public DateTimeOffset Since, LastNotified;
        public AlertSeverity NotifiedSeverity;
        public int ClearCount;
    }

    private readonly AlertOptions _options;
    private readonly IReadOnlyList<IAlertSink> _sinks;
    private readonly string _application;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, Active> _active = new(StringComparer.Ordinal);
    private long _dropped;

    public AlertManager(AlertOptions options, IReadOnlyList<IAlertSink> sinks, string application, ILogger logger)
    {
        _options = options;
        _sinks = sinks;
        _application = application;
        _logger = logger;
    }

    /// <summary>Conditions that crossed a limit and have not cleared yet (for a health endpoint).</summary>
    public IReadOnlyList<Alert> ActiveAlerts(DateTimeOffset now)
    {
        lock (_lock) return _active.Values.Select(a => ToAlert(a.Condition, AlertState.Firing, now, a.Since)).ToList();
    }

    /// <summary>Conditions that were not stored because <see cref="AlertOptions.MaxActiveAlerts"/> was reached.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    private Alert ToAlert(AlertCondition c, AlertState state, DateTimeOffset now, DateTimeOffset since) =>
        new(c.Key, c.Kind, c.Severity, state, c.Title, c.Message, c.Source, c.Destination, c.Value, c.Threshold, now, since, _application);

    /// <summary>Applies one evaluation result and sends whatever has to be sent.</summary>
    public async Task ProcessAsync(IReadOnlyList<AlertCondition> conditions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var toSend = new List<Alert>();
        lock (_lock)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in conditions)
            {
                if (c.Severity < _options.MinSeverity) continue;
                seen.Add(c.Key);
                if (!_active.TryGetValue(c.Key, out var a))
                {
                    if (_active.Count >= _options.MaxActiveAlerts)
                    {
                        if (Interlocked.Increment(ref _dropped) == 1)
                            _logger.LogWarning("Alert tracking is full ({Max} conditions); further conditions are counted but not announced.", _options.MaxActiveAlerts);
                        continue;
                    }
                    a = new Active { Condition = c, Since = now, LastNotified = now, NotifiedSeverity = c.Severity };
                    _active[c.Key] = a;
                    toSend.Add(ToAlert(c, AlertState.Firing, now, a.Since));
                    continue;
                }

                a.ClearCount = 0;
                a.Condition = c;
                var escalated = c.Severity > a.NotifiedSeverity;
                if (escalated || now - a.LastNotified >= _options.Cooldown)
                {
                    a.LastNotified = now;
                    a.NotifiedSeverity = c.Severity;
                    toSend.Add(ToAlert(c, AlertState.Firing, now, a.Since));
                }
            }

            foreach (var key in _active.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var a = _active[key];
                if (++a.ClearCount < _options.ClearEvaluations) continue;
                _active.Remove(key);
                if (_options.NotifyOnResolved)
                    toSend.Add(ToAlert(a.Condition, AlertState.Resolved, now, a.Since));
            }
        }

        foreach (var alert in toSend)
            await SendAsync(alert, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        foreach (var sink in _sinks)
        {
            Exception? last = null;
            var sent = false;
            for (var attempt = 1; attempt <= _options.Retry.MaxAttempts && !sent; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.Retry.AttemptTimeout);
                try
                {
                    await sink.SendAsync(alert, timeout.Token).ConfigureAwait(false);
                    sent = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;   // shutting down
                }
                catch (Exception ex)
                {
                    last = ex is OperationCanceledException ? new TimeoutException($"Attempt timed out after {_options.Retry.AttemptTimeout}.", ex) : ex;
                    if (!RetryPolicy.IsTransient(last) || attempt >= _options.Retry.MaxAttempts) break;
                    try { await _options.Retry.Delay(_options.Retry.DelayFor(attempt), cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                }
            }
            if (!sent)
                _logger.LogError("Could not send alert '{Key}' ({State}) to {Sink}: {Error}", alert.Key, alert.State, sink.Name,
                    last == null ? "unknown error" : ReportDeliveryService.Describe(last));
        }
    }
}
