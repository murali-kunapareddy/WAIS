using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace InviteApp;

public record ContactDto(string? Honorific, string? Name, string? Phone, string? Groups, List<string>? GroupList, string? Notes);
public record CsvImportDto(string? CsvContent);
public record CampaignCreateDto(
    string? Name,
    string? MessageTemplate,
    string? MediaPath,
    string? TargetGroups,
    List<string>? Groups,
    string? TargetMode,
    List<int>? ContactIds,
    DateTime? ScheduledAt,
    bool? StartImmediately,
    bool? SaveAsDraft);
public record SendTestDto(
    string? Phone,
    string? Honorific,
    string? Name,
    string? MessageTemplate,
    string? MediaPath,
    int? CampaignId);

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // SQLite invites.db
        var dbPath = Path.Combine(builder.Environment.ContentRootPath, "invites.db");
        builder.Services.AddDbContext<InviteDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        // HttpClient for wa-bridge sidecar on 127.0.0.1:3001
        builder.Services.AddHttpClient("WaBridge", client =>
        {
            client.BaseAddress = new Uri("http://127.0.0.1:3001");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        // Register scheduler service
        builder.Services.AddSingleton<CampaignSchedulerService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CampaignSchedulerService>());

        var app = builder.Build();

        // Ensure SQLite database is created and apply lightweight column upgrades
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InviteDbContext>();
            db.Database.EnsureCreated();
            EnsureSchemaUpgrades(db);
        }

        app.UseDefaultFiles();
        app.UseStaticFiles();

        // =====================================================================
        // WHATSAPP BRIDGE STATUS & RECONNECT PROXY
        // =====================================================================
        var bridgeLaunchLock = new object();
        var lastBridgeLaunchUtc = DateTime.MinValue;

        app.MapGet("/api/bridge/status", async (IHttpClientFactory httpClientFactory) =>
        {
            try
            {
                var client = httpClientFactory.CreateClient("WaBridge");
                using var response = await client.GetAsync("/status");
                var json = await response.Content.ReadAsStringAsync();
                return Results.Content(json, "application/json");
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    state = "BRIDGE_OFFLINE",
                    connected = false,
                    qr = (string?)null,
                    qrDataUrl = (string?)null,
                    error = $"wa-bridge on 127.0.0.1:3001 is unreachable: {ex.Message}"
                });
            }
        });

        app.MapPost("/api/bridge/connect", async (IHttpClientFactory httpClientFactory, IWebHostEnvironment env) =>
        {
            try
            {
                var client = httpClientFactory.CreateClient("WaBridge");
                using var response = await client.PostAsJsonAsync("/connect", new { force = false });
                var json = await response.Content.ReadAsStringAsync();
                return Results.Content(json, "application/json", statusCode: (int)response.StatusCode);
            }
            catch (Exception bridgeEx)
            {
                // wa-bridge is offline on port 3001: attempt to start it locally on Windows
                var bridgeDir = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "wa-bridge"));
                var bridgeScript = Path.Combine(bridgeDir, "bridge.js");

                if (!File.Exists(bridgeScript))
                {
                    return Results.Json(new
                    {
                        success = false,
                        state = "BRIDGE_OFFLINE",
                        connected = false,
                        error = $"wa-bridge is offline and '{bridgeScript}' was not found: {bridgeEx.Message}"
                    }, statusCode: 503);
                }

                bool shouldLaunch = false;
                lock (bridgeLaunchLock)
                {
                    if ((DateTime.UtcNow - lastBridgeLaunchUtc).TotalSeconds >= 10)
                    {
                        lastBridgeLaunchUtc = DateTime.UtcNow;
                        shouldLaunch = true;
                    }
                }

                if (shouldLaunch)
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = "/k title WA-Bridge (Port 3001) && node bridge.js",
                            WorkingDirectory = bridgeDir,
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Normal
                        };
                        Process.Start(psi);
                    }
                    catch (Exception startEx)
                    {
                        return Results.Json(new
                        {
                            success = false,
                            state = "BRIDGE_OFFLINE",
                            connected = false,
                            error = $"Failed to launch wa-bridge automatically: {startEx.Message}"
                        }, statusCode: 503);
                    }
                }

                return Results.Json(new
                {
                    success = true,
                    startedBridge = true,
                    state = "INITIALIZING",
                    connected = false,
                    message = "Starting WhatsApp Bridge (port 3001) and initializing connection..."
                });
            }
        });

        // =====================================================================
        // CONTACTS CRUD + MULTI-TAG GROUPS + CSV IMPORT/EXPORT
        // =====================================================================
        app.MapGet("/api/contacts", async (string? group, string? search, InviteDbContext db) =>
        {
            var contacts = await db.Contacts
                .OrderBy(c => c.Name)
                .ThenBy(c => c.Id)
                .ToListAsync();

            if (!string.IsNullOrWhiteSpace(group))
            {
                contacts = contacts
                    .Where(c => c.GetGroupList().Contains(group.Trim(), StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var q = search.Trim();
                contacts = contacts
                    .Where(c =>
                        c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        c.Phone.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        c.Honorific.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        c.Groups.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        c.Notes.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return Results.Ok(contacts.Select(c => new
            {
                c.Id,
                c.Honorific,
                c.Name,
                c.Phone,
                c.Groups,
                GroupList = c.GetGroupList(),
                c.Notes,
                c.CreatedAt
            }));
        });

        app.MapGet("/api/groups", async (InviteDbContext db) =>
        {
            var allGroups = await db.Contacts
                .Select(c => c.Groups)
                .ToListAsync();

            var groupCounts = allGroups
                .SelectMany(g => g.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Name = g.First(), Count = g.Count() })
                .OrderBy(g => g.Name)
                .ToList();

            return Results.Ok(groupCounts);
        });

        app.MapPost("/api/contacts", async (ContactDto dto, InviteDbContext db) =>
        {
            var normalizedPhone = Helpers.NormalizePhone(dto.Phone);
            if (string.IsNullOrWhiteSpace(normalizedPhone) || normalizedPhone.Length < 10)
            {
                return Results.BadRequest(new { error = "A valid phone number is required." });
            }

            // Duplicate phone check
            var existing = await db.Contacts.FirstOrDefaultAsync(c => c.Phone == normalizedPhone);
            if (existing != null)
            {
                return Results.Conflict(new
                {
                    error = $"Duplicate phone number: {normalizedPhone} already exists for '{existing.Name}'.",
                    existingContactId = existing.Id
                });
            }

            var rawNotes = (dto.Notes ?? string.Empty).Trim();
            if (rawNotes.Length > 150)
            {
                return Results.BadRequest(new { error = "Notes cannot exceed 150 characters." });
            }

            var contact = new Contact
            {
                Honorific = (dto.Honorific ?? string.Empty).Trim(),
                Name = (dto.Name ?? string.Empty).Trim(),
                Phone = normalizedPhone,
                Groups = Helpers.NormalizeGroups(dto.Groups, dto.GroupList),
                Notes = rawNotes,
                CreatedAt = DateTime.UtcNow
            };

            db.Contacts.Add(contact);
            await db.SaveChangesAsync();

            return Results.Created($"/api/contacts/{contact.Id}", new
            {
                contact.Id,
                contact.Honorific,
                contact.Name,
                contact.Phone,
                contact.Groups,
                GroupList = contact.GetGroupList(),
                contact.Notes,
                contact.CreatedAt
            });
        });

        app.MapPut("/api/contacts/{id:int}", async (int id, ContactDto dto, InviteDbContext db) =>
        {
            var contact = await db.Contacts.FindAsync(id);
            if (contact == null)
            {
                return Results.NotFound(new { error = "Contact not found." });
            }

            var normalizedPhone = Helpers.NormalizePhone(dto.Phone);
            if (string.IsNullOrWhiteSpace(normalizedPhone) || normalizedPhone.Length < 10)
            {
                return Results.BadRequest(new { error = "A valid phone number is required." });
            }

            // Duplicate phone check excluding current contact
            var duplicate = await db.Contacts.FirstOrDefaultAsync(c => c.Phone == normalizedPhone && c.Id != id);
            if (duplicate != null)
            {
                return Results.Conflict(new
                {
                    error = $"Duplicate phone number: {normalizedPhone} already belongs to '{duplicate.Name}'.",
                    existingContactId = duplicate.Id
                });
            }

            var rawNotes = (dto.Notes ?? string.Empty).Trim();
            if (rawNotes.Length > 150)
            {
                return Results.BadRequest(new { error = "Notes cannot exceed 150 characters." });
            }

            contact.Honorific = (dto.Honorific ?? string.Empty).Trim();
            contact.Name = (dto.Name ?? string.Empty).Trim();
            contact.Phone = normalizedPhone;
            contact.Groups = Helpers.NormalizeGroups(dto.Groups, dto.GroupList);
            contact.Notes = rawNotes;

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                contact.Id,
                contact.Honorific,
                contact.Name,
                contact.Phone,
                contact.Groups,
                GroupList = contact.GetGroupList(),
                contact.Notes,
                contact.CreatedAt
            });
        });

        app.MapDelete("/api/contacts/{id:int}", async (int id, InviteDbContext db) =>
        {
            var contact = await db.Contacts.FindAsync(id);
            if (contact == null)
            {
                return Results.NotFound(new { error = "Contact not found." });
            }

            db.Contacts.Remove(contact);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = true, id });
        });

        // Bulk CSV Import (supports multipart file upload, raw text/csv body, or JSON { csvContent })
        app.MapPost("/api/contacts/import", async (HttpRequest request, InviteDbContext db) =>
        {
            string csvText = string.Empty;

            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync();
                var file = form.Files.FirstOrDefault();
                if (file != null)
                {
                    using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
                    csvText = await reader.ReadToEndAsync();
                }
                else if (form.TryGetValue("csvContent", out var formCsv))
                {
                    csvText = formCsv.ToString();
                }
            }
            else if (request.ContentType != null && request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                var dto = await request.ReadFromJsonAsync<CsvImportDto>();
                csvText = dto?.CsvContent ?? string.Empty;
            }
            else
            {
                using var reader = new StreamReader(request.Body, Encoding.UTF8);
                csvText = await reader.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(csvText))
            {
                return Results.BadRequest(new { error = "CSV content is empty." });
            }

            var parsedRows = CsvHelper.ParseContactsCsv(csvText);
            var existingContacts = await db.Contacts.ToDictionaryAsync(c => c.Phone, StringComparer.Ordinal);

            int imported = 0;
            int updatedGroups = 0;
            int skippedDuplicates = 0;
            int invalidRows = 0;

            foreach (var row in parsedRows)
            {
                var normalizedPhone = Helpers.NormalizePhone(row.Phone);
                if (string.IsNullOrWhiteSpace(normalizedPhone) || normalizedPhone.Length < 10)
                {
                    invalidRows++;
                    continue;
                }

                var normalizedGroups = Helpers.NormalizeGroups(row.Groups);
                var normalizedNotes = Helpers.NormalizeNotes(row.Notes, 150);

                if (existingContacts.TryGetValue(normalizedPhone, out var existing))
                {
                    // Merge any new groups into existing contact if provided, while preventing duplicate phone records
                    var mergedGroups = Helpers.NormalizeGroups(existing.Groups, new[] { normalizedGroups });
                    if (!string.Equals(existing.Groups, mergedGroups, StringComparison.Ordinal))
                    {
                        existing.Groups = mergedGroups;
                        updatedGroups++;
                    }
                    if (string.IsNullOrWhiteSpace(existing.Honorific) && !string.IsNullOrWhiteSpace(row.Honorific))
                    {
                        existing.Honorific = row.Honorific.Trim();
                    }
                    if (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(row.Name))
                    {
                        existing.Name = row.Name.Trim();
                    }
                    if (string.IsNullOrWhiteSpace(existing.Notes) && !string.IsNullOrWhiteSpace(normalizedNotes))
                    {
                        existing.Notes = normalizedNotes;
                    }
                    skippedDuplicates++;
                    continue;
                }

                var newContact = new Contact
                {
                    Honorific = row.Honorific.Trim(),
                    Name = row.Name.Trim(),
                    Phone = normalizedPhone,
                    Groups = normalizedGroups,
                    Notes = normalizedNotes,
                    CreatedAt = DateTime.UtcNow
                };

                db.Contacts.Add(newContact);
                existingContacts[normalizedPhone] = newContact;
                imported++;
            }

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                imported,
                skippedDuplicates,
                updatedGroups,
                invalidRows,
                totalProcessed = parsedRows.Count
            });
        });

        // Bulk CSV Export (Honorific,Name,Phone,Groups)
        app.MapGet("/api/contacts/export", async (string? group, InviteDbContext db) =>
        {
            var contacts = await db.Contacts
                .OrderBy(c => c.Name)
                .ThenBy(c => c.Id)
                .ToListAsync();

            if (!string.IsNullOrWhiteSpace(group))
            {
                contacts = contacts
                    .Where(c => c.GetGroupList().Contains(group.Trim(), StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }

            var csv = CsvHelper.ExportContactsCsv(contacts);
            var bytes = Encoding.UTF8.GetBytes(csv);
            return Results.File(bytes, "text/csv; charset=utf-8", "contacts.csv");
        });

        // =====================================================================
        // CAMPAIGNS API + DEDUPLICATION + TEST TO MYSELF + RETRY FAILED
        // =====================================================================
        app.MapGet("/api/campaigns", async (InviteDbContext db) =>
        {
            var campaigns = await db.Campaigns
                .Include(c => c.Recipients)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            return Results.Ok(campaigns.Select(FormatCampaignSummary));
        });

        app.MapGet("/api/campaigns/{id:int}", async (int id, InviteDbContext db) =>
        {
            var campaign = await db.Campaigns
                .Include(c => c.Recipients)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (campaign == null)
            {
                return Results.NotFound(new { error = "Campaign not found." });
            }

            return Results.Ok(FormatCampaignDetail(campaign));
        });

        // Preview deduplicated recipient count across selected overlapping groups OR specific individuals
        app.MapPost("/api/campaigns/preview-recipients", async (CampaignCreateDto dto, InviteDbContext db) =>
        {
            var isIndividualMode = string.Equals(dto.TargetMode, "individuals", StringComparison.OrdinalIgnoreCase);
            var selectedGroups = Helpers.NormalizeGroups(dto.TargetGroups, dto.Groups)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var allContacts = await db.Contacts.ToListAsync();
            var matched = isIndividualMode
                ? FilterAndDeduplicateByIds(allContacts, dto.ContactIds)
                : FilterAndDeduplicateContacts(allContacts, selectedGroups);

            return Results.Ok(new
            {
                targetMode = isIndividualMode ? "individuals" : "groups",
                selectedGroups = isIndividualMode ? Array.Empty<string>() : selectedGroups,
                totalUniqueRecipients = matched.Count,
                recipients = matched.Select(c => new
                {
                    c.Id,
                    c.Honorific,
                    c.Name,
                    c.Phone,
                    c.Groups,
                    c.Notes
                })
            });
        });

        // Create Campaign (deduplicating recipients across overlapping selected groups OR specific individuals)
        app.MapPost("/api/campaigns", async (CampaignCreateDto dto, InviteDbContext db) =>
        {
            var name = (dto.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest(new { error = "Campaign name is required." });
            }

            var messageTemplate = (dto.MessageTemplate ?? string.Empty).Trim();
            var mediaPath = Helpers.CleanMediaPath(dto.MediaPath);

            if (string.IsNullOrWhiteSpace(messageTemplate) && string.IsNullOrWhiteSpace(mediaPath))
            {
                return Results.BadRequest(new { error = "Either a message template or a media path is required." });
            }

            var isIndividualMode = string.Equals(dto.TargetMode, "individuals", StringComparison.OrdinalIgnoreCase);
            var targetGroups = isIndividualMode ? string.Empty : Helpers.NormalizeGroups(dto.TargetGroups, dto.Groups);
            var selectedGroupList = targetGroups
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var allContacts = await db.Contacts.ToListAsync();
            List<Contact> uniqueContacts;

            if (isIndividualMode)
            {
                if (dto.ContactIds == null || dto.ContactIds.Count == 0)
                {
                    return Results.BadRequest(new { error = "Please select at least one individual contact." });
                }
                uniqueContacts = FilterAndDeduplicateByIds(allContacts, dto.ContactIds);
                if (uniqueContacts.Count == 0)
                {
                    return Results.BadRequest(new { error = "None of the selected individual contacts were found." });
                }
                targetGroups = $"Individuals ({uniqueContacts.Count})";
            }
            else
            {
                uniqueContacts = FilterAndDeduplicateContacts(allContacts, selectedGroupList);
                if (uniqueContacts.Count == 0)
                {
                    return Results.BadRequest(new { error = "No contacts matched the selected groups." });
                }
            }

            var status = "Draft";
            DateTime? startedAt = null;
            var scheduledAtUtc = dto.ScheduledAt.HasValue
                ? DateTime.SpecifyKind(dto.ScheduledAt.Value.ToUniversalTime(), DateTimeKind.Utc)
                : (DateTime?)null;

            if (dto.SaveAsDraft == true)
            {
                status = "Draft";
            }
            else if (dto.StartImmediately == true)
            {
                status = "Running";
                startedAt = DateTime.UtcNow;
            }
            else if (scheduledAtUtc.HasValue)
            {
                status = "Scheduled";
            }

            var campaign = new Campaign
            {
                Name = name,
                MessageTemplate = messageTemplate,
                MediaPath = mediaPath,
                TargetGroups = targetGroups,
                TargetMode = isIndividualMode ? "individuals" : "groups",
                TargetContactIds = isIndividualMode ? string.Join(",", uniqueContacts.Select(c => c.Id)) : string.Empty,
                Status = status,
                ScheduledAt = scheduledAtUtc,
                CreatedAt = DateTime.UtcNow,
                StartedAt = startedAt,
                CurrentActivity = status == "Running"
                    ? "Queued to start..."
                    : status == "Draft"
                        ? "Saved draft (ready to edit or send)"
                        : null
            };

            foreach (var contact in uniqueContacts)
            {
                campaign.Recipients.Add(new CampaignRecipient
                {
                    ContactId = contact.Id,
                    Honorific = contact.Honorific,
                    Name = contact.Name,
                    Phone = contact.Phone,
                    ResolvedMessage = Helpers.RenderTemplate(messageTemplate, contact.Honorific, contact.Name, contact.Phone),
                    Status = "Pending"
                });
            }

            db.Campaigns.Add(campaign);
            await db.SaveChangesAsync();

            return Results.Created($"/api/campaigns/{campaign.Id}", FormatCampaignDetail(campaign));
        });

        // Update a saved Draft Campaign in place
        app.MapPut("/api/campaigns/{id:int}", async (int id, CampaignCreateDto dto, InviteDbContext db) =>
        {
            var campaign = await db.Campaigns
                .Include(c => c.Recipients)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (campaign == null)
            {
                return Results.NotFound(new { error = "Campaign not found." });
            }

            if (!string.Equals(campaign.Status, "Draft", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Only Draft campaigns can be edited in place. Use 'Create & Launch / Schedule' to send a new campaign." });
            }

            var name = (dto.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest(new { error = "Campaign name is required." });
            }

            var messageTemplate = (dto.MessageTemplate ?? string.Empty).Trim();
            var mediaPath = Helpers.CleanMediaPath(dto.MediaPath);
            if (string.IsNullOrWhiteSpace(messageTemplate) && string.IsNullOrWhiteSpace(mediaPath))
            {
                return Results.BadRequest(new { error = "Either a message template or a media path is required." });
            }

            var isIndividualMode = string.Equals(dto.TargetMode, "individuals", StringComparison.OrdinalIgnoreCase);
            var targetGroups = isIndividualMode ? string.Empty : Helpers.NormalizeGroups(dto.TargetGroups, dto.Groups);
            var selectedGroupList = targetGroups
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var allContacts = await db.Contacts.ToListAsync();
            List<Contact> uniqueContacts;

            if (isIndividualMode)
            {
                if (dto.ContactIds == null || dto.ContactIds.Count == 0)
                {
                    return Results.BadRequest(new { error = "Please select at least one individual contact." });
                }
                uniqueContacts = FilterAndDeduplicateByIds(allContacts, dto.ContactIds);
                if (uniqueContacts.Count == 0)
                {
                    return Results.BadRequest(new { error = "None of the selected individual contacts were found." });
                }
                targetGroups = $"Individuals ({uniqueContacts.Count})";
            }
            else
            {
                uniqueContacts = FilterAndDeduplicateContacts(allContacts, selectedGroupList);
                if (uniqueContacts.Count == 0)
                {
                    return Results.BadRequest(new { error = "No contacts matched the selected groups." });
                }
            }

            var scheduledAtUtc = dto.ScheduledAt.HasValue
                ? DateTime.SpecifyKind(dto.ScheduledAt.Value.ToUniversalTime(), DateTimeKind.Utc)
                : (DateTime?)null;

            campaign.Name = name;
            campaign.MessageTemplate = messageTemplate;
            campaign.MediaPath = mediaPath;
            campaign.TargetGroups = targetGroups;
            campaign.TargetMode = isIndividualMode ? "individuals" : "groups";
            campaign.TargetContactIds = isIndividualMode ? string.Join(",", uniqueContacts.Select(c => c.Id)) : string.Empty;
            campaign.ScheduledAt = scheduledAtUtc;
            campaign.CurrentActivity = "Saved draft (updated)";

            db.CampaignRecipients.RemoveRange(campaign.Recipients);
            campaign.Recipients.Clear();

            foreach (var contact in uniqueContacts)
            {
                campaign.Recipients.Add(new CampaignRecipient
                {
                    ContactId = contact.Id,
                    Honorific = contact.Honorific,
                    Name = contact.Name,
                    Phone = contact.Phone,
                    ResolvedMessage = Helpers.RenderTemplate(messageTemplate, contact.Honorific, contact.Name, contact.Phone),
                    Status = "Pending"
                });
            }

            await db.SaveChangesAsync();

            return Results.Ok(FormatCampaignDetail(campaign));
        });

        // Start / Resume a Campaign
        app.MapPost("/api/campaigns/{id:int}/start", async (int id, InviteDbContext db) =>
        {
            var campaign = await db.Campaigns
                .Include(c => c.Recipients)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (campaign == null)
            {
                return Results.NotFound(new { error = "Campaign not found." });
            }

            var hasPending = campaign.Recipients.Any(r => r.Status == "Pending" || r.Status == "Sending");
            if (!hasPending)
            {
                return Results.BadRequest(new { error = "No pending recipients left to send in this campaign." });
            }

            campaign.Status = "Running";
            campaign.StartedAt ??= DateTime.UtcNow;
            campaign.CompletedAt = null;
            campaign.CurrentActivity = "Resuming campaign...";
            await db.SaveChangesAsync();

            return Results.Ok(FormatCampaignDetail(campaign));
        });

        // Pause a running Campaign
        app.MapPost("/api/campaigns/{id:int}/pause", async (int id, InviteDbContext db) =>
        {
            var campaign = await db.Campaigns
                .Include(c => c.Recipients)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (campaign == null)
            {
                return Results.NotFound(new { error = "Campaign not found." });
            }

            campaign.Status = "Paused";
            campaign.CurrentActivity = "Paused by user";
            await db.SaveChangesAsync();

            return Results.Ok(FormatCampaignDetail(campaign));
        });

        // Retry Failed recipients in a Campaign
        app.MapPost("/api/campaigns/{id:int}/retry-failed", async (int id, InviteDbContext db) =>
        {
            var campaign = await db.Campaigns
                .Include(c => c.Recipients)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (campaign == null)
            {
                return Results.NotFound(new { error = "Campaign not found." });
            }

            var failedRecipients = campaign.Recipients.Where(r => r.Status == "Failed").ToList();
            if (failedRecipients.Count == 0)
            {
                return Results.BadRequest(new { error = "There are no failed recipients to retry in this campaign." });
            }

            foreach (var r in failedRecipients)
            {
                r.Status = "Pending";
                r.ErrorMessage = null;
                r.SentAt = null;
                r.ResolvedMessage = Helpers.RenderTemplate(campaign.MessageTemplate, r.Honorific, r.Name, r.Phone);
            }

            campaign.Status = "Running";
            campaign.CompletedAt = null;
            campaign.StartedAt ??= DateTime.UtcNow;
            campaign.CurrentActivity = $"Retrying {failedRecipients.Count} failed recipient(s)...";

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                retriedCount = failedRecipients.Count,
                campaign = FormatCampaignDetail(campaign)
            });
        });

        // Send Test to Myself (either standalone via /api/campaigns/send-test or for a specific campaign)
        app.MapPost("/api/campaigns/send-test", async (
            SendTestDto dto,
            InviteDbContext db,
            CampaignSchedulerService scheduler,
            IHttpClientFactory httpClientFactory) =>
        {
            string? messageTemplate = dto.MessageTemplate;
            string? mediaPath = dto.MediaPath;

            if (dto.CampaignId.HasValue)
            {
                var campaign = await db.Campaigns.FindAsync(dto.CampaignId.Value);
                if (campaign != null)
                {
                    if (string.IsNullOrWhiteSpace(messageTemplate)) messageTemplate = campaign.MessageTemplate;
                    if (string.IsNullOrWhiteSpace(mediaPath)) mediaPath = campaign.MediaPath;
                }
            }

            string targetPhone = Helpers.NormalizePhone(dto.Phone);

            // If no phone explicitly passed, attempt to fetch connected WhatsApp user's own number from wa-bridge /status
            if (string.IsNullOrWhiteSpace(targetPhone))
            {
                try
                {
                    var client = httpClientFactory.CreateClient("WaBridge");
                    using var statusResp = await client.GetAsync("/status");
                    if (statusResp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await statusResp.Content.ReadAsStringAsync());
                        if (doc.RootElement.TryGetProperty("user", out var userProp) && userProp.ValueKind == JsonValueKind.String)
                        {
                            targetPhone = Helpers.NormalizePhone(userProp.GetString());
                        }
                    }
                }
                catch
                {
                    // Fallback to validation error below
                }
            }

            if (string.IsNullOrWhiteSpace(targetPhone) || targetPhone.Length < 10)
            {
                return Results.BadRequest(new { error = "Please provide a valid phone number for 'Send Test to Myself' (or ensure WhatsApp is connected)." });
            }

            var honorific = string.IsNullOrWhiteSpace(dto.Honorific) ? "Mr." : dto.Honorific.Trim();
            var name = string.IsNullOrWhiteSpace(dto.Name) ? "Test Guest" : dto.Name.Trim();
            var renderedMessage = Helpers.RenderTemplate(messageTemplate ?? string.Empty, honorific, name, targetPhone);
            var cleanedMedia = Helpers.CleanMediaPath(mediaPath);

            if (string.IsNullOrWhiteSpace(renderedMessage) && string.IsNullOrWhiteSpace(cleanedMedia))
            {
                return Results.BadRequest(new { error = "Test message or media path cannot both be empty." });
            }

            var (success, error) = await scheduler.SendViaBridgeAsync(targetPhone, renderedMessage, cleanedMedia);
            if (!success)
            {
                return Results.BadRequest(new { success = false, error = error ?? "Failed to send test message via bridge." });
            }

            return Results.Ok(new
            {
                success = true,
                phone = targetPhone,
                renderedMessage,
                mediaPath = cleanedMedia
            });
        });

        app.MapPost("/api/campaigns/{id:int}/send-test", async (
            int id,
            SendTestDto? dto,
            InviteDbContext db,
            CampaignSchedulerService scheduler,
            IHttpClientFactory httpClientFactory) =>
        {
            var campaign = await db.Campaigns.FindAsync(id);
            if (campaign == null)
            {
                return Results.NotFound(new { error = "Campaign not found." });
            }

            var mergedDto = new SendTestDto(
                dto?.Phone,
                dto?.Honorific,
                dto?.Name,
                string.IsNullOrWhiteSpace(dto?.MessageTemplate) ? campaign.MessageTemplate : dto.MessageTemplate,
                string.IsNullOrWhiteSpace(dto?.MediaPath) ? campaign.MediaPath : dto.MediaPath,
                id);

            string targetPhone = Helpers.NormalizePhone(mergedDto.Phone);
            if (string.IsNullOrWhiteSpace(targetPhone))
            {
                try
                {
                    var client = httpClientFactory.CreateClient("WaBridge");
                    using var statusResp = await client.GetAsync("/status");
                    if (statusResp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await statusResp.Content.ReadAsStringAsync());
                        if (doc.RootElement.TryGetProperty("user", out var userProp) && userProp.ValueKind == JsonValueKind.String)
                        {
                            targetPhone = Helpers.NormalizePhone(userProp.GetString());
                        }
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(targetPhone) || targetPhone.Length < 10)
            {
                return Results.BadRequest(new { error = "Please provide a valid phone number for 'Send Test to Myself' (or ensure WhatsApp is connected)." });
            }

            var honorific = string.IsNullOrWhiteSpace(mergedDto.Honorific) ? "Mr." : mergedDto.Honorific.Trim();
            var name = string.IsNullOrWhiteSpace(mergedDto.Name) ? "Test Guest" : mergedDto.Name.Trim();
            var renderedMessage = Helpers.RenderTemplate(mergedDto.MessageTemplate ?? string.Empty, honorific, name, targetPhone);
            var cleanedMedia = Helpers.CleanMediaPath(mergedDto.MediaPath);

            var (success, error) = await scheduler.SendViaBridgeAsync(targetPhone, renderedMessage, cleanedMedia);
            if (!success)
            {
                return Results.BadRequest(new { success = false, error = error ?? "Failed to send test message via bridge." });
            }

            return Results.Ok(new
            {
                success = true,
                phone = targetPhone,
                renderedMessage,
                mediaPath = cleanedMedia
            });
        });

        app.MapDelete("/api/campaigns/{id:int}", async (int id, InviteDbContext db) =>
        {
            var campaign = await db.Campaigns.FindAsync(id);
            if (campaign == null)
            {
                return Results.NotFound(new { error = "Campaign not found." });
            }

            db.Campaigns.Remove(campaign);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = true, id });
        });

        app.Run();
    }

    /// <summary>
    /// Safely upgrades existing SQLite databases created before newer columns were added.
    /// </summary>
    private static void EnsureSchemaUpgrades(InviteDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var shouldClose = conn.State != System.Data.ConnectionState.Open;
        if (shouldClose) conn.Open();
        try
        {
            EnsureColumnExists(conn, "Contacts", "Notes", "TEXT NOT NULL DEFAULT ''");
            EnsureColumnExists(conn, "Campaigns", "TargetMode", "TEXT NOT NULL DEFAULT 'groups'");
            EnsureColumnExists(conn, "Campaigns", "TargetContactIds", "TEXT NOT NULL DEFAULT ''");
        }
        finally
        {
            if (shouldClose) conn.Close();
        }
    }

    private static void EnsureColumnExists(System.Data.Common.DbConnection conn, string tableName, string columnName, string columnDef)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        var exists = false;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDef};";
            alterCmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Filters contacts matching specific contact IDs and deduplicates by normalized Phone.
    /// </summary>
    private static List<Contact> FilterAndDeduplicateByIds(IEnumerable<Contact> allContacts, IReadOnlyCollection<int>? contactIds)
    {
        if (contactIds == null || contactIds.Count == 0)
        {
            return new List<Contact>();
        }

        var idSet = new HashSet<int>(contactIds);
        return allContacts
            .Where(c => idSet.Contains(c.Id) && !string.IsNullOrWhiteSpace(c.Phone))
            .GroupBy(c => Helpers.NormalizePhone(c.Phone), StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id)
            .ToList();
    }

    /// <summary>
    /// Filters contacts matching any of the selected groups (or all contacts if none specified)
    /// and deduplicates by normalized Phone so contacts belonging to multiple overlapping groups
    /// only receive the campaign invite once.
    /// </summary>
    private static List<Contact> FilterAndDeduplicateContacts(IEnumerable<Contact> allContacts, IReadOnlyCollection<string> selectedGroups)
    {
        IEnumerable<Contact> query = allContacts;

        if (selectedGroups.Count > 0)
        {
            query = query.Where(c =>
            {
                var contactGroups = c.GetGroupList();
                return contactGroups.Any(cg => selectedGroups.Contains(cg, StringComparer.OrdinalIgnoreCase));
            });
        }

        return query
            .Where(c => !string.IsNullOrWhiteSpace(c.Phone))
            .GroupBy(c => Helpers.NormalizePhone(c.Phone), StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id)
            .ToList();
    }

    private static DateTime? AsUtc(DateTime? dt) =>
        dt.HasValue ? DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc) : null;

    private static DateTime AsUtc(DateTime dt) =>
        DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    private static string ResolveTargetMode(Campaign c)
    {
        if (string.Equals(c.TargetMode, "individuals", StringComparison.OrdinalIgnoreCase))
            return "individuals";
        if (!string.IsNullOrWhiteSpace(c.TargetContactIds))
            return "individuals";
        if (!string.IsNullOrWhiteSpace(c.TargetGroups) && c.TargetGroups.StartsWith("Individuals (", StringComparison.OrdinalIgnoreCase))
            return "individuals";
        return "groups";
    }

    private static List<int> ResolveTargetContactIds(Campaign c, string targetMode)
    {
        if (!string.IsNullOrWhiteSpace(c.TargetContactIds))
        {
            return c.TargetContactIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
        }

        if (targetMode == "individuals")
        {
            return c.Recipients
                .Where(r => r.ContactId.HasValue)
                .Select(r => r.ContactId!.Value)
                .Distinct()
                .ToList();
        }

        return new List<int>();
    }

    private static object FormatCampaignSummary(Campaign c)
    {
        int total = c.Recipients.Count;
        int sent = c.Recipients.Count(r => r.Status == "Sent");
        int failed = c.Recipients.Count(r => r.Status == "Failed");
        int pending = c.Recipients.Count(r => r.Status == "Pending" || r.Status == "Sending");
        var targetMode = ResolveTargetMode(c);
        var targetContactIds = ResolveTargetContactIds(c, targetMode);
        var targetGroupList = targetMode == "individuals"
            ? Array.Empty<string>()
            : c.TargetGroups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new
        {
            c.Id,
            c.Name,
            c.MessageTemplate,
            c.MediaPath,
            c.TargetGroups,
            TargetMode = targetMode,
            TargetGroupList = targetGroupList,
            TargetContactIds = targetContactIds,
            c.Status,
            ScheduledAt = AsUtc(c.ScheduledAt),
            CreatedAt = AsUtc(c.CreatedAt),
            StartedAt = AsUtc(c.StartedAt),
            CompletedAt = AsUtc(c.CompletedAt),
            c.CurrentActivity,
            TotalRecipients = total,
            SentCount = sent,
            FailedCount = failed,
            PendingCount = pending
        };
    }

    private static object FormatCampaignDetail(Campaign c)
    {
        int total = c.Recipients.Count;
        int sent = c.Recipients.Count(r => r.Status == "Sent");
        int failed = c.Recipients.Count(r => r.Status == "Failed");
        int pending = c.Recipients.Count(r => r.Status == "Pending" || r.Status == "Sending");
        var targetMode = ResolveTargetMode(c);
        var targetContactIds = ResolveTargetContactIds(c, targetMode);
        var targetGroupList = targetMode == "individuals"
            ? Array.Empty<string>()
            : c.TargetGroups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new
        {
            c.Id,
            c.Name,
            c.MessageTemplate,
            c.MediaPath,
            c.TargetGroups,
            TargetMode = targetMode,
            TargetGroupList = targetGroupList,
            TargetContactIds = targetContactIds,
            c.Status,
            ScheduledAt = AsUtc(c.ScheduledAt),
            CreatedAt = AsUtc(c.CreatedAt),
            StartedAt = AsUtc(c.StartedAt),
            CompletedAt = AsUtc(c.CompletedAt),
            c.CurrentActivity,
            TotalRecipients = total,
            SentCount = sent,
            FailedCount = failed,
            PendingCount = pending,
            Recipients = c.Recipients.OrderBy(r => r.Id).Select(r => new
            {
                r.Id,
                r.ContactId,
                r.Honorific,
                r.Name,
                r.Phone,
                r.ResolvedMessage,
                r.Status,
                r.ErrorMessage,
                SentAt = AsUtc(r.SentAt)
            })
        };
    }
}
