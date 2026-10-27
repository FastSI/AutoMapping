using System.Text;
using AutoMapping;
using AutoMapping.OData;
using AutoMapping.Reporting;
using AutoMapping.Reporting.Sftp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoMapping.Tests;

[Collection("ProcessEnvironment")]   // one test sets DOTNET_ENVIRONMENT for the whole process, so it must not overlap SftpTests
public class HardeningTests
{
    private static readonly IMapper Mapper = new MapperConfiguration(c => c.CreateMap<OdOrder, OdOrderDto>()).CreateMapper();
    private static IQueryable<OdOrder> Data() => new[] { new OdOrder { Id = 1, Total = 1m } }.AsQueryable();

    private static void Query(string query, ODataSettings? settings = null) =>
        Data().Get<OdOrder, OdOrderDto>(Mapper, ODataQueryOptions.Parse(query), settings);

    [Fact]
    public void OData_limits_reject_expensive_or_invalid_queries()
    {
        Assert.Throws<ODataException>(() => Query("$skip=5", new ODataSettings { MaxSkip = 4 }));
        Assert.Throws<ODataException>(() => Query("$orderby=Id,Total,Rating", new ODataSettings { MaxListItems = 2 }));
        Assert.Throws<ODataException>(() => Query("$select=Id,Total,Rating", new ODataSettings { MaxListItems = 2 }));
        Assert.Throws<ODataException>(() => Query("$search=aaa bbb ccc", new ODataSettings { MaxSearchTerms = 2 }));
        Assert.Throws<ODataException>(() => Query("$search=ab"));                       // shorter than MinSearchTermLength
        Assert.Throws<ODataException>(() => Query("$filter=Id div 0 eq 1"));            // constant zero divisor
        Assert.Throws<ODataException>(() => Query("$filter=Id mod 0 eq 1"));
        Query("$skip=4&$orderby=Id,Total", new ODataSettings { MaxSkip = 4 });          // at the limit is fine
    }

    [Fact]
    public void Telemetry_redacts_exception_messages_and_drops_them_if_the_redactor_fails()
    {
        static Exception Thrown(string message)
        {
            try { throw new InvalidOperationException(message); }
            catch (Exception ex) { return ex; }
        }

        var redacted = new MapperTelemetry(new TelemetryOptions { CaptureExceptionMessages = true, MessageRedactor = m => m.Replace("secret", "***") });
        redacted.OnFailed(typeof(string), typeof(int), 1, Thrown("a secret b"));
        Assert.Equal("a *** b", Assert.Single(redacted.Snapshot().Exceptions).Message);

        var broken = new MapperTelemetry(new TelemetryOptions { CaptureExceptionMessages = true, MessageRedactor = _ => throw new InvalidOperationException() });
        broken.OnFailed(typeof(string), typeof(int), 1, Thrown("a secret b"));
        Assert.Null(Assert.Single(broken.Snapshot().Exceptions).Message);
    }

    [Fact]
    public void Outbox_moves_entries_with_a_tampered_manifest_to_the_dead_letter_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AutoMapping-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var outbox = new ReportOutbox(new ReportOutbox.Options { Directory = dir }, NullLogger.Instance);
            var context = new ReportDeliveryContext("app", now, now, "Daily-0");
            outbox.Enqueue("sink", context, new[] { new ReportFile("AutoMapping-app-20260101T000000Z.json", "application/json", new byte[] { 1 }) }, "boom", now);
            Assert.Equal(1, outbox.PendingCount);

            var entry = Directory.GetDirectories(Path.Combine(dir, "pending")).Single();
            var manifest = Path.Combine(entry, "manifest.json");
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("AutoMapping-app-20260101T000000Z.json", "../escape.json"));

            var delivery = new ReportDeliveryService(new RetryPolicy(), NullLogger.Instance);
            var result = outbox.ProcessAsync(_ => null, delivery, now.AddHours(1), CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal((0, 0, 1), result);
            Assert.Equal(0, outbox.PendingCount);
            Assert.Single(Directory.GetDirectories(Path.Combine(dir, "dead")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Sftp_accept_any_host_key_is_refused_in_production()
    {
        var previous = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            var options = new SftpOptions { Host = "h", Username = "u", Password = "p", AcceptAnyHostKey = true };
            Assert.Throws<ReportingConfigurationException>(() => new SftpReportSink(options, null!));
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previous); }
    }
}
