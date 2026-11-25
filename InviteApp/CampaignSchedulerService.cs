using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace InviteApp;

public class CampaignSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CampaignSchedulerService> _logger;

    // Rule 5: Windows 11 Sleep Guard via P/Invoke SetThreadExecutionState
    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_AWAYMODE_REQUIRED = 0x00000040,
        ES_CONTINUOUS = 0x80000000,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_SYSTEM_REQUIRED = 0x00000001
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

    private bool _sleepGuardActive = false;

    public CampaignSchedulerService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<CampaignSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private void EnableSleepGuard()
    {
        if (!_sleepGuardActive && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED);
            _sleepGuardActive = true;
            _logger.LogInformation("[SleepGuard] Windows sleep prevention ENABLED (ES_CONTINUOUS | ES_SYSTEM_REQUIRED).");
        }
    }

    private void ReleaseSleepGuard()
    {
        if (_sleepGuardActive && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
            _sleepGuardActive = false;
            _logger.LogInformation("[SleepGuard] Windows sleep prevention RELEASED (ES_CONTINUOUS).");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CampaignSchedulerService started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessCampaignsTickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in CampaignSchedulerService main loop.");
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        finally
        {
            ReleaseSleepGuard();
        }
    }

    private async Task ProcessCampaignsTickAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InviteDbContext>();

        var now = DateTime.UtcNow;

        // Promote due Scheduled campaigns to Running
        var dueCampaigns = await db.Campaigns
            .Where(c => c.Status == "Scheduled" && (c.ScheduledAt == null || c.ScheduledAt <= now))
            .ToListAsync(stoppingToken);

        foreach (var due in dueCampaigns)
        {
            due.Status = "Running";
            due.StartedAt ??= now;
            due.CurrentActivity = "Starting scheduled campaign...";
        }

        if (dueCampaigns.Count > 0)
        {
            await db.SaveChangesAsync(stoppingToken);
        }

        // Find active Running campaign
        var activeCampaign = await db.Campaigns
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync(c => c.Status == "Running", stoppingToken);

        if (activeCampaign == null)
        {
            ReleaseSleepGuard();
            return;
        }

        // Rule 5: Keep system awake while a campaign is Running
        EnableSleepGuard();

        await RunCampaignLoopAsync(activeCampaign.Id, stoppingToken);
    }

    private async Task RunCampaignLoopAsync(int campaignId, CancellationToken stoppingToken)
    {
        int messagesSentInSession = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<InviteDbContext>();

            var campaign = await db.Campaigns.FindAsync(new object[] { campaignId }, stoppingToken);
            if (campaign == null || campaign.Status != "Running")
            {
                break;
            }

            var nextRecipient = await db.CampaignRecipients
                .Where(r => r.CampaignId == campaignId && (r.Status == "Pending" || r.Status == "Sending"))
                .OrderBy(r => r.Id)
                .FirstOrDefaultAsync(stoppingToken);

            if (nextRecipient == null)
            {
                campaign.Status = "Completed";
                campaign.CompletedAt = DateTime.UtcNow;
                campaign.CurrentActivity = "Completed";
                await db.SaveChangesAsync(stoppingToken);
                _logger.LogInformation("Campaign {Id} ({Name}) completed.", campaign.Id, campaign.Name);
                break;
            }

            // Mark as Sending
            nextRecipient.Status = "Sending";
            var personalizedText = !string.IsNullOrWhiteSpace(nextRecipient.ResolvedMessage)
                ? nextRecipient.ResolvedMessage
                : Helpers.RenderTemplate(campaign.MessageTemplate, nextRecipient.Honorific, nextRecipient.Name, nextRecipient.Phone);
            nextRecipient.ResolvedMessage = personalizedText;
            campaign.CurrentActivity = $"Sending to {nextRecipient.Name} ({nextRecipient.Phone})...";
            await db.SaveChangesAsync(stoppingToken);

            // Send via wa-bridge on 127.0.0.1:3001
            var (success, error) = await SendViaBridgeAsync(
                nextRecipient.Phone,
                personalizedText,
                Helpers.CleanMediaPath(campaign.MediaPath),
                stoppingToken);

            if (success)
            {
                nextRecipient.Status = "Sent";
                nextRecipient.SentAt = DateTime.UtcNow;
                nextRecipient.ErrorMessage = null;
            }
            else
            {
                nextRecipient.Status = "Failed";
                nextRecipient.ErrorMessage = error;
            }

            messagesSentInSession++;
            await db.SaveChangesAsync(stoppingToken);

            // Check if any more pending recipients remain before pacing delay
            var hasMorePending = await db.CampaignRecipients
                .AnyAsync(r => r.CampaignId == campaignId && r.Status == "Pending", stoppingToken);

            if (!hasMorePending)
            {
                campaign.Status = "Completed";
                campaign.CompletedAt = DateTime.UtcNow;
                campaign.CurrentActivity = "Completed";
                await db.SaveChangesAsync(stoppingToken);
                _logger.LogInformation("Campaign {Id} ({Name}) finished all recipients.", campaign.Id, campaign.Name);
                break;
            }

            // Rule 4: Anti-Ban Human Pacing
            // 3-minute cooldown pause after every 20 messages in a campaign, otherwise random 15000ms - 40000ms delay
            int delayMs;
            if (messagesSentInSession % 20 == 0)
            {
                delayMs = 180_000; // 3 minutes
                campaign.CurrentActivity = $"Batch cooldown pause (3 mins after {messagesSentInSession} messages)...";
                _logger.LogInformation("Campaign {Id}: 20-message batch reached ({Count}). Pausing for 3 minutes.", campaignId, messagesSentInSession);
            }
            else
            {
                delayMs = Random.Shared.Next(15000, 40001);
                campaign.CurrentActivity = $"Waiting {delayMs / 1000}s before next recipient (anti-ban pacing)...";
                _logger.LogInformation("Campaign {Id}: Pacing delay {DelayMs}ms before next message.", campaignId, delayMs);
            }
            await db.SaveChangesAsync(stoppingToken);

            // Wait in 1-second slices so Pause/Cancel takes effect immediately
            int elapsed = 0;
            while (elapsed < delayMs && !stoppingToken.IsCancellationRequested)
            {
                var step = Math.Min(1000, delayMs - elapsed);
                await Task.Delay(step, stoppingToken);
                elapsed += step;

                using var checkScope = _scopeFactory.CreateScope();
                var checkDb = checkScope.ServiceProvider.GetRequiredService<InviteDbContext>();
                var currentStatus = await checkDb.Campaigns
                    .Where(c => c.Id == campaignId)
                    .Select(c => c.Status)
                    .FirstOrDefaultAsync(stoppingToken);

                if (currentStatus != "Running")
                {
                    _logger.LogInformation("Campaign {Id} status changed to {Status} during delay. Exiting loop.", campaignId, currentStatus);
                    return;
                }
            }
        }
    }

    public async Task<(bool Success, string? Error)> SendViaBridgeAsync(
        string phone,
        string message,
        string mediaPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("WaBridge");
            var payload = new
            {
                phone = Helpers.NormalizePhone(phone),
                message = message,
                caption = message,
                mediaPath = Helpers.CleanMediaPath(mediaPath)
            };

            using var response = await client.PostAsJsonAsync("/send", payload, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var errProp))
                {
                    return (false, errProp.GetString() ?? $"Bridge returned {(int)response.StatusCode}");
                }
            }
            catch
            {
                // Ignore JSON parse error
            }

            return (false, $"HTTP {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            return (false, $"Bridge communication error: {ex.Message}");
        }
    }
}
