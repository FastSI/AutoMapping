using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using AutoMapping.Reporting;

namespace AutoMapping.Alerting;

/// <summary>Writes alerts to the application log.</summary>
public sealed class LogAlertSink : IAlertSink
{
    private readonly ILogger _logger;

    public LogAlertSink(ILoggerFactory factory, string name = "log")
    {
        ArgumentNullException.ThrowIfNull(factory);
        _logger = factory.CreateLogger("AutoMapping.Alerts");
        Name = name;
    }

    public string Name { get; }

    public Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        if (alert.State == AlertState.Resolved)
            _logger.LogInformation("Resolved: {Title} (since {Since:o}). {Message}", alert.Title, alert.FiringSince, alert.Message);
        else if (alert.Severity == AlertSeverity.Critical)
            _logger.LogError("CRITICAL: {Title}. {Message}", alert.Title, alert.Message);
        else
            _logger.LogWarning("WARNING: {Title}. {Message}", alert.Title, alert.Message);
        return Task.CompletedTask;
    }
}

/// <summary>Runs your delegate for every alert.</summary>
public sealed class CallbackAlertSink : IAlertSink
{
    private readonly Func<Alert, CancellationToken, Task> _callback;

    public CallbackAlertSink(Func<Alert, CancellationToken, Task> callback, string name = "callback")
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        Name = name;
    }

    public string Name { get; }

    public Task SendAsync(Alert alert, CancellationToken cancellationToken) => _callback(alert, cancellationToken);
}

/// <summary>POSTs the alert as JSON to an HTTPS endpoint. With a secret, the request is signed exactly like report webhooks:
/// <c>X-AutoMapping-Signature</c> is <c>sha256=</c> + hex HMAC-SHA256 over <c>"{X-AutoMapping-Timestamp}.{body}"</c>.</summary>
public sealed class WebhookAlertSink : IAlertSink
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Uri _url;
    private readonly HttpClient _client;
    private readonly byte[]? _secret;

    public WebhookAlertSink(Uri url, string? signingSecret = null, HttpClient? client = null, bool allowInsecureHttp = false, string name = "webhook")
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme != Uri.UriSchemeHttps && !(allowInsecureHttp && url.Scheme == Uri.UriSchemeHttp))
            throw new AlertConfigurationException("The webhook URL must use https.");
        _url = url;
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _secret = string.IsNullOrEmpty(signingSecret) ? null : Encoding.UTF8.GetBytes(signingSecret);
        Name = name;
    }

    public string Name { get; }

    public async Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(alert, Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-AutoMapping-Application", ReportRenderer.Safe(alert.Application));
        request.Headers.Add("X-AutoMapping-Alert", ReportRenderer.Safe(alert.State.ToString().ToLowerInvariant()));
        if (_secret != null)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var payload = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
            request.Headers.Add("X-AutoMapping-Timestamp", timestamp);
            request.Headers.Add("X-AutoMapping-Signature", "sha256=" + Convert.ToHexString(HMACSHA256.HashData(_secret, payload)).ToLowerInvariant());
        }

        try
        {
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return;
            var code = (int)response.StatusCode;
            throw new ReportDeliveryException(Name, $"The endpoint answered HTTP {code}.", code >= 500 || code is 408 or 429);
        }
        catch (HttpRequestException ex)
        {
            throw new ReportDeliveryException(Name, $"The request failed: {ex.Message}", true, ex);
        }
    }

    public override string ToString() => $"{Name} ({_url.Host})";   // never the path or secret: they may contain tokens
}

public sealed class SmtpAlertOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary>TLS for the connection. On by default; turn it off only for a trusted local relay.</summary>
    public bool UseSsl { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "";
    public List<string> To { get; } = new();
    public string SubjectPrefix { get; set; } = "[AutoMapping]";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Sends one mail message. Replaceable in tests.</summary>
public interface ISmtpSender
{
    Task SendAsync(SmtpAlertOptions options, MailMessage message, CancellationToken cancellationToken);
}

internal sealed class SystemNetSmtpSender : ISmtpSender
{
    public async Task SendAsync(SmtpAlertOptions options, MailMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.UseSsl,
            Timeout = (int)Math.Min(int.MaxValue, options.Timeout.TotalMilliseconds)
        };
        if (!string.IsNullOrEmpty(options.UserName))
            client.Credentials = new NetworkCredential(options.UserName, options.Password);
        // SendMailAsync only takes a token on .NET 10; WaitAsync gives the same cancellation behaviour on every target
        await client.SendMailAsync(message).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Emails alerts. The mail contains the alert text only (type names and numbers); the password never appears in messages or logs.</summary>
public sealed class SmtpAlertSink : IAlertSink
{
    private readonly SmtpAlertOptions _options;
    private readonly ISmtpSender _sender;
    private readonly MailAddress _from;
    private readonly IReadOnlyList<MailAddress> _to;

    public SmtpAlertSink(SmtpAlertOptions options, ISmtpSender? sender = null, string name = "email")
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Host)) throw new AlertConfigurationException("SMTP Host is required.");
        if (options.Port is < 1 or > 65535) throw new AlertConfigurationException("SMTP Port must be between 1 and 65535.");
        if (options.To.Count == 0) throw new AlertConfigurationException("At least one SMTP recipient (To) is required.");
        try
        {
            _from = new MailAddress(options.From);
            _to = options.To.Select(a => new MailAddress(a)).ToList();
        }
        catch (FormatException)
        {
            throw new AlertConfigurationException("SMTP From and To must be valid e-mail addresses.");
        }
        _options = options;
        _sender = sender ?? new SystemNetSmtpSender();
        Name = name;
    }

    public string Name { get; }

    public async Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        using var message = new MailMessage { From = _from, Subject = Subject(alert), Body = Body(alert), IsBodyHtml = false };
        foreach (var to in _to) message.To.Add(to);
        try
        {
            await _sender.SendAsync(_options, message, cancellationToken).ConfigureAwait(false);
        }
        catch (SmtpException ex)
        {
            var permanent = ex.StatusCode is SmtpStatusCode.ClientNotPermitted or SmtpStatusCode.MustIssueStartTlsFirst or
                SmtpStatusCode.MailboxNameNotAllowed or SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.UserNotLocalTryAlternatePath or
                SmtpStatusCode.SyntaxError or SmtpStatusCode.CommandNotImplemented;
            // the message is replaced on purpose: server replies can echo the user name
            throw new ReportDeliveryException(Name, $"The mail server refused the message ({ex.StatusCode}).", !permanent);
        }
    }

    private string Subject(Alert a)
    {
        var state = a.State == AlertState.Resolved ? "RESOLVED" : a.Severity.ToString().ToUpperInvariant();
        // one line only: a line break in a header could inject extra headers
        return $"{_options.SubjectPrefix} {state}: {a.Title} ({a.Application})".ReplaceLineEndings(" ");
    }

    private static string Body(Alert a)
    {
        var sb = new StringBuilder();
        sb.AppendLine(a.Title);
        sb.AppendLine();
        sb.AppendLine(a.Message);
        sb.AppendLine();
        sb.AppendLine($"Application: {a.Application}");
        sb.AppendLine($"State: {a.State}");
        sb.AppendLine($"Severity: {a.Severity}");
        sb.AppendLine($"Kind: {a.Kind}");
        sb.AppendLine($"Firing since (UTC): {a.FiringSince.UtcDateTime:u}");
        sb.AppendLine($"Sent (UTC): {a.At.UtcDateTime:u}");
        return sb.ToString();
    }

    public override string ToString() => $"{Name} ({_options.Host})";   // never the credentials
}
