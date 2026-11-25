# WhatsApp Invite Studio (Windows 11 Local App)

## Architecture & Tech Stack
- **OS:** Windows 11 (`127.0.0.1` localhost only, zero external cloud/paid APIs).
- **Backend & Scheduler:** .NET 10 (`ASP.NET Core Minimal API` + `EF Core SQLite` database `invites.db`).
- **Frontend:** Single-page HTML5 + Bootstrap 5.3 + Vanilla JS in `InviteApp/wwwroot/index.html`.
- **WhatsApp Bridge:** Node.js sidecar (`wa-bridge`) using `whatsapp-web.js` (`^1.34.8`) + `express` on port `3001`.

## Mandatory WhatsApp & Windows Nitty-Gritty Rules
1. **LID & JID Resolution:** Never construct `@c.us` strings manually. Always call `const numberId = await client.getNumberId(digits)` first and send to `numberId._serialized` (prevents `No LID for user` errors).
2. **Phone Normalization:** Strip all non-digits. If length is 10, auto-prepend `91` (India) both when saving contacts in .NET and before calling `getNumberId()` in Node.js.
3. **Safe Typing Simulation:** Always wrap `chat.sendStateTyping()` in a `try/catch` block because brand-new chats without an open thread throw `Evaluation failed`.
4. **Anti-Ban Human Pacing:** Random delay between `15000ms` and `40000ms` between messages, plus a 3-minute cooldown pause after every 20 messages in a campaign.
5. **Windows 11 Sleep Guard:** Use P/Invoke `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` in the .NET `BackgroundService` while a campaign is `Running`, releasing it when finished.
6. **Media Path Cleanup:** Strip accidental surrounding quotes (`"C:\path\card.jpg"`) before calling `MessageMedia.fromFilePath()`.
