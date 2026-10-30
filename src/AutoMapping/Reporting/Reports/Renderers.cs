using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoMapping.Reporting;

public enum ReportFormat { Json, Html, Markdown, Csv }

/// <summary>One file ready to be written or uploaded.</summary>
public sealed record ReportFile(string FileName, string ContentType, byte[] Content);

public static class ReportRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Base file name without extension, e.g. <c>AutoMapping-orders-api-20261026T000000Z</c>.</summary>
    public static string BaseName(DiagnosticsReport report) =>
        $"AutoMapping-{Safe(report.Application)}-{report.PeriodEnd.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";

    /// <summary>Letters, digits, '-' and '_' only: the name ends up in file paths and SFTP paths, so nothing else may pass.</summary>
    public static string Safe(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        var result = sb.ToString().Trim('-');
        return result.Length == 0 ? "app" : result.Length > 64 ? result[..64] : result;
    }

    public static IReadOnlyList<ReportFile> Render(DiagnosticsReport report, IEnumerable<ReportFormat> formats)
    {
        var name = BaseName(report);
        var files = new List<ReportFile>();
        foreach (var format in formats.Distinct())
        {
            switch (format)
            {
                case ReportFormat.Json:
                    files.Add(new(name + ".json", "application/json", JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions)));
                    break;
                case ReportFormat.Html:
                    files.Add(new(name + ".html", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Html(report))));
                    break;
                case ReportFormat.Markdown:
                    files.Add(new(name + ".md", "text/markdown; charset=utf-8", Encoding.UTF8.GetBytes(Markdown(report))));
                    break;
                case ReportFormat.Csv:
                    files.Add(new(name + "-performance.csv", "text/csv; charset=utf-8", Encoding.UTF8.GetBytes(PerformanceCsv(report))));
                    files.Add(new(name + "-exceptions.csv", "text/csv; charset=utf-8", Encoding.UTF8.GetBytes(ExceptionsCsv(report))));
                    break;
            }
        }
        return files;
    }

    // ------------------------------------------------------------------ CSV (formula-injection safe)

    private static string Csv(object? value)
    {
        var text = value switch { null => "", double d => d.ToString("0.###", CultureInfo.InvariantCulture), DateTimeOffset o => o.UtcDateTime.ToString("o", CultureInfo.InvariantCulture), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
        // spreadsheets run cells that start with = + - @ as formulas: neutralise them
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) text = "'" + text;
        return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    private static string PerformanceCsv(DiagnosticsReport r)
    {
        var sb = new StringBuilder("source,destination,calls,errors,error_rate,avg_us,p50_us,p95_us,p99_us,max_ms_since_start\r\n");
        foreach (var p in r.Performance)
            sb.Append(string.Join(',', new object[] { p.Source, p.Destination, p.Calls, p.Errors, p.ErrorRate, p.AverageMicroseconds, p.P50Microseconds, p.P95Microseconds, p.P99Microseconds, p.MaxMillisecondsSinceStart }.Select(Csv))).Append("\r\n");
        return sb.ToString();
    }

    private static string ExceptionsCsv(DiagnosticsReport r)
    {
        var sb = new StringBuilder("exception_type,source,destination,count,first_seen_utc,last_seen_utc,top_frames,message\r\n");
        foreach (var e in r.Exceptions)
            sb.Append(string.Join(',', new object?[] { e.ExceptionType, e.Source, e.Destination, e.Count, e.FirstSeen, e.LastSeen, string.Join(" < ", e.TopFrames), e.Message }.Select(Csv))).Append("\r\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Markdown

    private static string Md(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Markdown(DiagnosticsReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# AutoMapping report: {Md(r.Application)}");
        sb.AppendLine();
        sb.AppendLine($"Period: {r.PeriodStart.UtcDateTime:u} to {r.PeriodEnd.UtcDateTime:u} (UTC). Generated {r.GeneratedAt.UtcDateTime:u}. Overall status: **{r.WorstSeverity}**.");
        sb.AppendLine();
        sb.AppendLine("## Findings");
        if (r.Findings.Count == 0) sb.AppendLine("None. Everything is within the configured thresholds.");
        foreach (var f in r.Findings) sb.AppendLine($"- **{f.Severity}** `{f.Code}` {Md(f.Title)}: {Md(f.Detail)}");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        var s = r.Summary;
        sb.AppendLine($"- Calls: {s.TotalCalls:N0} ({s.CallsPerSecond:F2}/s), errors: {s.TotalErrors:N0} ({s.ErrorRate:P2}), average {s.AverageMilliseconds * 1000:F1} µs");
        sb.AppendLine($"- Maps observed: {s.MapsObserved}, exception groups: {s.ExceptionGroups}");
        sb.AppendLine();
        sb.AppendLine("## Performance (top maps by calls)");
        sb.AppendLine("| Map | Calls | Errors | Avg µs | p50 µs | p95 µs | p99 µs |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var p in r.Performance)
            sb.AppendLine($"| {Md(p.Source)} → {Md(p.Destination)} | {p.Calls:N0} | {p.Errors:N0} | {p.AverageMicroseconds:F1} | {p.P50Microseconds:F0} | {p.P95Microseconds:F0} | {p.P99Microseconds:F0} |");
        sb.AppendLine();
        sb.AppendLine("## Exceptions");
        if (r.Exceptions.Count == 0) sb.AppendLine("None in this period.");
        else
        {
            sb.AppendLine("| Exception | Map | Count | Last seen (UTC) | Where |");
            sb.AppendLine("|---|---|---:|---|---|");
            foreach (var e in r.Exceptions)
                sb.AppendLine($"| {Md(e.ExceptionType)} | {Md(e.Source)} → {Md(e.Destination)} | {e.Count:N0} | {e.LastSeen.UtcDateTime:u} | {Md(string.Join(" < ", e.TopFrames))} |");
        }
        sb.AppendLine();
        var m = r.Memory;
        sb.AppendLine("## Memory and GC");
        sb.AppendLine($"- Samples: {m.Samples}; working set {LeakAnalyzer.Bytes(m.WorkingSetMinBytes)} to {LeakAnalyzer.Bytes(m.WorkingSetMaxBytes)} (average {LeakAnalyzer.Bytes(m.WorkingSetAverageBytes)})");
        sb.AppendLine($"- Allocated in period: {LeakAnalyzer.Bytes(m.AllocatedBytesInPeriod)}; GCs gen0/1/2: {m.Gen0Collections}/{m.Gen1Collections}/{m.Gen2Collections}; GC pause avg {m.GcPauseAveragePercent:F1}% max {m.GcPauseMaxPercent:F1}%");
        if (m.CachesAtStart is { } a && m.CachesAtEnd is { } b)
            sb.AppendLine($"- Mapper caches: type maps {a.TypeMaps} → {b.TypeMaps}, mappings {a.Mappings} → {b.Mappings}, projections {a.Projections} → {b.Projections}");
        sb.AppendLine($"- Leak check: **{m.Leak.Verdict}**. {Md(m.Leak.Explanation)}");
        sb.AppendLine();
        sb.AppendLine($"_AutoMapping {Md(r.Environment.LibraryVersion)} on {Md(r.Environment.Runtime)}._");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ HTML (every dynamic value is encoded)

    private static string H(object? value) => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");

    private static string Html(DiagnosticsReport r)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
        sb.Append($"<title>AutoMapping report: {H(r.Application)}</title><style>");
        sb.Append("body{font:14px/1.5 system-ui,sans-serif;margin:2rem auto;max-width:1000px;padding:0 1rem;color:#1b1f23}h1{font-size:1.5rem}h2{margin-top:2rem;font-size:1.15rem;border-bottom:1px solid #d0d7de;padding-bottom:.3rem}");
        sb.Append("table{border-collapse:collapse;width:100%}th,td{padding:.35rem .6rem;border-bottom:1px solid #e5e8eb;text-align:left}td.n,th.n{text-align:right;font-variant-numeric:tabular-nums}");
        sb.Append(".b{display:inline-block;padding:0 .5rem;border-radius:9px;font-size:.8rem;color:#fff}.Info{background:#57606a}.Warning{background:#9a6700}.Critical{background:#cf222e}code{background:#f3f4f6;padding:0 .25rem;border-radius:3px}");
        sb.Append("@media(prefers-color-scheme:dark){body{background:#0d1117;color:#e6edf3}th,td,h2{border-color:#30363d}code{background:#21262d}}</style></head><body>");
        sb.Append($"<h1>AutoMapping report: {H(r.Application)}</h1>");
        sb.Append($"<p>Period {H(r.PeriodStart.UtcDateTime.ToString("u"))} to {H(r.PeriodEnd.UtcDateTime.ToString("u"))} (UTC). Overall status: <span class=\"b {r.WorstSeverity}\">{r.WorstSeverity}</span></p>");

        sb.Append("<h2>Findings</h2>");
        if (r.Findings.Count == 0) sb.Append("<p>None. Everything is within the configured thresholds.</p>");
        else
        {
            sb.Append("<ul>");
            foreach (var f in r.Findings) sb.Append($"<li><span class=\"b {f.Severity}\">{f.Severity}</span> <code>{H(f.Code)}</code> <strong>{H(f.Title)}</strong>: {H(f.Detail)}</li>");
            sb.Append("</ul>");
        }

        var s = r.Summary;
        sb.Append("<h2>Summary</h2><ul>");
        sb.Append($"<li>Calls: {s.TotalCalls:N0} ({s.CallsPerSecond:F2}/s); errors: {s.TotalErrors:N0} ({s.ErrorRate:P2}); average {s.AverageMilliseconds * 1000:F1} µs</li>");
        sb.Append($"<li>Maps observed: {s.MapsObserved}; exception groups: {s.ExceptionGroups}</li></ul>");

        sb.Append("<h2>Performance</h2><table><tr><th>Map</th><th class=\"n\">Calls</th><th class=\"n\">Errors</th><th class=\"n\">Avg µs</th><th class=\"n\">p50 µs</th><th class=\"n\">p95 µs</th><th class=\"n\">p99 µs</th></tr>");
        foreach (var p in r.Performance)
            sb.Append($"<tr><td>{H(p.Source)} → {H(p.Destination)}</td><td class=\"n\">{p.Calls:N0}</td><td class=\"n\">{p.Errors:N0}</td><td class=\"n\">{p.AverageMicroseconds:F1}</td><td class=\"n\">{p.P50Microseconds:F0}</td><td class=\"n\">{p.P95Microseconds:F0}</td><td class=\"n\">{p.P99Microseconds:F0}</td></tr>");
        sb.Append("</table>");

        sb.Append("<h2>Exceptions</h2>");
        if (r.Exceptions.Count == 0) sb.Append("<p>None in this period.</p>");
        else
        {
            sb.Append("<table><tr><th>Exception</th><th>Map</th><th class=\"n\">Count</th><th>Last seen (UTC)</th><th>Where</th></tr>");
            foreach (var e in r.Exceptions)
                sb.Append($"<tr><td>{H(e.ExceptionType)}</td><td>{H(e.Source)} → {H(e.Destination)}</td><td class=\"n\">{e.Count:N0}</td><td>{H(e.LastSeen.UtcDateTime.ToString("u"))}</td><td>{H(string.Join(" < ", e.TopFrames))}</td></tr>");
            sb.Append("</table>");
        }

        var m = r.Memory;
        sb.Append("<h2>Memory and GC</h2><ul>");
        sb.Append($"<li>Samples: {m.Samples}; working set {H(LeakAnalyzer.Bytes(m.WorkingSetMinBytes))} to {H(LeakAnalyzer.Bytes(m.WorkingSetMaxBytes))}</li>");
        sb.Append($"<li>Allocated in period: {H(LeakAnalyzer.Bytes(m.AllocatedBytesInPeriod))}; GCs gen0/1/2: {m.Gen0Collections}/{m.Gen1Collections}/{m.Gen2Collections}; GC pause avg {m.GcPauseAveragePercent:F1}% max {m.GcPauseMaxPercent:F1}%</li>");
        if (m.CachesAtStart is { } a && m.CachesAtEnd is { } b)
            sb.Append($"<li>Mapper caches: type maps {a.TypeMaps} → {b.TypeMaps}, mappings {a.Mappings} → {b.Mappings}, projections {a.Projections} → {b.Projections}</li>");
        sb.Append($"<li>Leak check: <strong>{m.Leak.Verdict}</strong>. {H(m.Leak.Explanation)}</li></ul>");
        sb.Append($"<p><small>AutoMapping {H(r.Environment.LibraryVersion)} on {H(r.Environment.Runtime)}. Generated {H(r.GeneratedAt.UtcDateTime.ToString("u"))}.</small></p></body></html>");
        return sb.ToString();
    }
}
