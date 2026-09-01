using System.Net.Mail;
using Dapper;
using GitSrv.Api.Data;
using Npgsql;

namespace GitSrv.Api.Collab;

public sealed class EmailOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 1025;
    public string From { get; init; } = "gitsrv@localhost";
    public string BaseUrl { get; init; } = "http://localhost:8080";
}

/// <summary>
/// Polls for unsent notifications and delivers one email per recipient batch. When no SMTP host is
/// configured it just marks them sent (dev/QA without a mail container). One digest email per user
/// per poll keeps volume sane.
/// </summary>
public sealed class EmailWorker(NpgsqlDataSource dataSource, EmailOptions options, ILogger<EmailWorker> logger)
    : BackgroundService
{
    private sealed record Pending(long Id, long UserId, string Email, string DisplayName, string Title, string Reason, string Body, string Url);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let migrations finish first.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Email worker poll failed"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var pending = (await conn.QueryAsync<Pending>("""
            SELECT n.id, n.user_id AS UserId, u.email AS Email, u.display_name AS DisplayName,
                   n.title, n.reason, n.body, n.url
            FROM notifications n JOIN users u ON u.id = n.user_id
            WHERE NOT n.email_sent AND n.created_at < now() - interval '20 seconds'
            ORDER BY n.user_id, n.created_at
            LIMIT 500
            """)).ToList();
        if (pending.Count == 0) return;

        foreach (var group in pending.GroupBy(p => p.UserId))
        {
            var items = group.ToList();
            var ok = await SendAsync(items, ct);
            if (ok)
                await conn.ExecuteAsync("UPDATE notifications SET email_sent = true WHERE id = ANY(@ids)",
                    new { ids = items.Select(i => i.Id).ToArray() });
        }
    }

    private async Task<bool> SendAsync(List<Pending> items, CancellationToken ct)
    {
        var to = items[0].Email;
        var subject = items.Count == 1
            ? $"[GitSrv] {items[0].Title}"
            : $"[GitSrv] {items.Count} updates";

        var lines = items.Select(i =>
            $"- ({i.Reason}) {i.Title}\n  {options.BaseUrl}/{i.Url.TrimStart('#', '/')}" +
            (string.IsNullOrWhiteSpace(i.Body) ? "" : $"\n  {Trim(i.Body, 200)}"));
        var body = $"Hi {items[0].DisplayName},\n\nYou have activity on GitSrv:\n\n{string.Join("\n\n", lines)}\n";

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            logger.LogInformation("Email (no SMTP host configured) to {To}: {Subject}", to, subject);
            return true;
        }

        try
        {
            using var client = new SmtpClient(options.Host, options.Port) { EnableSsl = false };
            using var msg = new MailMessage(options.From, to, subject, body);
            await client.SendMailAsync(msg, ct);
            logger.LogInformation("Sent notification email to {To} ({Count} item(s))", to, items.Count);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send email to {To}", to);
            return false;
        }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
