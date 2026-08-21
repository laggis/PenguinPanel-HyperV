using System.Net;
using System.Net.Mail;

namespace HyperVPanel.Services;

public sealed class EmailService(IConfiguration configuration)
{
    public bool IsEnabled => configuration.GetValue<bool>("Smtp:Enabled");

    public int TimeoutSeconds
    {
        get
        {
            var configured = configuration.GetValue("Smtp:TimeoutSeconds", 15);
            return Math.Clamp(configured, 5, 120);
        }
    }

    public string HostSummary
    {
        get
        {
            var host = configuration["Smtp:Host"];
            var port = configuration["Smtp:Port"] ?? "587";
            return string.IsNullOrWhiteSpace(host) ? "Not set" : $"{host}:{port}";
        }
    }

    public void ValidateConfiguration()
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("SMTP is disabled in appsettings.");
        }

        if (string.IsNullOrWhiteSpace(configuration["Smtp:Host"]))
        {
            throw new InvalidOperationException("SMTP host is not configured.");
        }

        if (string.IsNullOrWhiteSpace(configuration["Smtp:FromEmail"]))
        {
            throw new InvalidOperationException("SMTP FromEmail is not configured.");
        }
    }

    public async Task SendAsync(string? to, string subject, string body, string? htmlBody = null)
    {
        if (string.IsNullOrWhiteSpace(to))
        {
            return;
        }

        ValidateConfiguration();

        var host = configuration["Smtp:Host"]!;
        var fromEmail = configuration["Smtp:FromEmail"]!;
        var port = configuration.GetValue("Smtp:Port", 587);
        var enableSsl = configuration.GetValue("Smtp:EnableSsl", true);
        var username = configuration["Smtp:Username"];
        var password = configuration["Smtp:Password"];
        var fromName = configuration["Smtp:FromName"] ?? "PenguinPanel";

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = enableSsl,
            Timeout = TimeoutSeconds * 1000
        };

        if (!string.IsNullOrWhiteSpace(username))
        {
            client.Credentials = new NetworkCredential(username, password ?? string.Empty);
        }

        using var message = new MailMessage
        {
            From = new MailAddress(fromEmail, fromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(to.Trim());

        if (!string.IsNullOrWhiteSpace(htmlBody))
        {
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(body, null, "text/plain"));
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(htmlBody, null, "text/html"));
        }

        await client.SendMailAsync(message).WaitAsync(TimeSpan.FromSeconds(TimeoutSeconds + 5));
    }
}
