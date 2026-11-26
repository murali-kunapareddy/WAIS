# WhatsApp Invite Studio (WAIS)

A 100% local **Windows 11** desktop web application for creating, personalizing, scheduling, and sending WhatsApp wedding and event invitations—with **zero external cloud services or paid APIs**.

Everything runs on `127.0.0.1` using your own WhatsApp account linked via WhatsApp Web.

---

## Features

### 1. WhatsApp Connection & Recovery
- **Live Status Bar:** Displays real-time WhatsApp readiness (`WhatsApp is ready (+...)`, `Scan QR to Link WhatsApp`, `Connecting...`, or disconnected/offline states).
- **In-App QR Scanner:** Automatically displays a scannable WhatsApp QR code whenever the session is unlinked.
- **One-Click `Connect again...`:** Re-initializes the WhatsApp session cleanly—and if the local `wa-bridge` sidecar isn't running on port `3001`, the backend automatically launches it.

### 2. Contact Manager
- **Guest Profiles:** Store `Honorific`, `Name`, `Phone`, multiple `Groups`, and `Notes` (free text up to **150 characters** with a live character counter).
- **Automatic Phone Normalization & Deduplication:** Strips non-digits, auto-prepends `91` (India) to 10-digit numbers, and prevents duplicate phone records.
- **Dynamic Group Filter & Picker:** Search and filter existing groups in real time to select them with consistent naming, or create a new group tag inline.
- **Guest Directory Search & Filter Pills:** Filter guests by group pill or search across name, honorific, phone, groups, and notes.
- **Bulk CSV Import / Export:** Upload `.csv` files (`Honorific,Name,Phone,Groups,Notes`), download a ready-to-use **Sample CSV**, or export your full contact list. Duplicate phone numbers are skipped while merging any new groups and filling blank fields.

### 3. Invitation Composer
- **Flexible Recipient Targeting:**
  - **Select by Groups:** Filter and select one or more groups (or leave all unchecked for All Contacts). Guests in overlapping groups are automatically deduplicated so everyone receives **at most 1 message**.
  - **Select Individuals:** Live search across name, phone, groups, and notes with multi-select checkboxes, selected guest chips, `Select Filtered`, and `Clear` actions.
- **Personalization & Spintax:** Supports `{{Honorific}}`, `{{Name}}`, `{{FullName}}`, `{{Phone}}`, and randomized `{Spintax|Options}` per recipient.
- **Media Attachments:** Attach a local image, PDF, or video by pasting its Windows file path (surrounding quotes from *Copy as path* are stripped automatically).
- **Live WhatsApp Preview:** Preview rendered messages and shuffle Spintax variants in real time.
- **Send Test to Myself:** Send a real test invitation (with media) to your own number before launching a campaign.
- **Save, Edit & Reuse Campaigns:**
  - Save configurations with **Save as Draft** and update drafts via **Update Draft**.
  - Load any saved draft or past campaign via the **Saved Campaigns** dropdown or **Edit & Reuse** button in the Campaign Monitor to modify text, media, schedule date, or recipients.
  - Launching or scheduling an edited/reused campaign always creates a **brand-new campaign**, preserving previous delivery logs.
- **Immediate Launch or Scheduled Delivery:** Leave schedule blank to send immediately, or pick a local date & time for the background scheduler.

### 4. Campaign Monitor & Reliability Safeguards
- **Live Progress & Per-Recipient Logs:** Tracks Sent, Failed, Pending counts, current pacing activity, and exact per-recipient error messages.
- **Playback Controls:** `Start / Resume`, `Pause`, `Retry Failed`, `Edit & Reuse`, and `Delete`.
- **Anti-Ban Human Pacing:** Random **15–40 second** delay between messages plus a **3-minute cooldown pause** after every **20 messages**.
- **LID / JID Resolution & Safe Typing:** Resolves WhatsApp IDs via `client.getNumberId()` (avoiding `No LID for user` errors) and wraps typing simulation safely for brand-new chats.
- **Windows 11 Sleep Guard:** Uses Win32 `SetThreadExecutionState` in the background scheduler to prevent Windows from sleeping while a campaign is actively running, releasing the guard once finished.

---

## Technologies Used

| Layer | Technologies |
| --- | --- |
| **OS / Runtime** | Windows 11 (`127.0.0.1` localhost only) |
| **Backend & Scheduler** | .NET 10, ASP.NET Core Minimal API, Entity Framework Core, SQLite (`invites.db`), `BackgroundService`, Kernel32 P/Invoke |
| **Frontend UI** | Single-page HTML5, Bootstrap 5.3, Bootstrap Icons, Vanilla JavaScript (`InviteApp/wwwroot/index.html`) |
| **WhatsApp Bridge (`wa-bridge`)** | Node.js, Express (`^4.21.2`), `whatsapp-web.js` (`^1.34.8`), `qrcode` (`^1.5.4`), local Chrome / Microsoft Edge via Puppeteer & `LocalAuth` |
| **Launcher** | PowerShell (`start-studio.ps1`) |

### Project Structure

```text
.
├── start-studio.ps1                  # One-click Windows 11 launcher
├── InviteApp/
│   ├── InviteApp.csproj              # .NET 10 web project (EF Core SQLite)
│   ├── Program.cs                    # Minimal API endpoints & SQLite schema upgrades
│   ├── Data.cs                       # EF Core entities (Contact, Campaign, CampaignRecipient) & helpers
│   ├── CampaignSchedulerService.cs   # Background sender, anti-ban pacing & Win11 sleep guard
│   ├── CsvHelper.cs                  # RFC-style CSV import/export parser
│   ├── appsettings.json              # App & logging configuration (127.0.0.1:5050)
│   └── wwwroot/index.html            # Single-page Bootstrap 5.3 frontend UI
└── wa-bridge/
    ├── package.json                  # Node.js sidecar dependencies
    └── bridge.js                     # Express + whatsapp-web.js local bridge (127.0.0.1:3001)
```

---

## Running the Application

### Prerequisites
- **Windows 11**
- **.NET 10 SDK** (`dotnet --version`)
- **Node.js** (LTS recommended, `node --version`)
- **Google Chrome** or **Microsoft Edge** installed (auto-detected by `wa-bridge`)

### First-Time Setup

1. Clone the repository:
   ```powershell
   git clone https://github.com/murali-kunapareddy/WAIS.git
   cd WAIS
   ```

2. Install Node.js dependencies for the WhatsApp bridge:
   ```powershell
   cd wa-bridge
   npm install
   cd ..
   ```

3. Restore .NET dependencies for the backend:
   ```powershell
   dotnet restore InviteApp\InviteApp.csproj
   ```

### Quick Start (Recommended)

Run the PowerShell launcher from the repository root:

```powershell
.\start-studio.ps1
```

This automatically:
1. Starts `wa-bridge` on `http://127.0.0.1:3001` in its own console window.
2. Starts `InviteApp` on `http://127.0.0.1:5050` in its own console window.
3. Opens `http://127.0.0.1:5050` in your default browser once the server is listening.

### Manual Startup (Alternative)

Open two terminals:

**Terminal 1 — WhatsApp Bridge (`127.0.0.1:3001`):**
```powershell
cd wa-bridge
node bridge.js
```

**Terminal 2 — .NET Backend (`127.0.0.1:5050`):**
```powershell
cd InviteApp
dotnet run --urls http://127.0.0.1:5050
```

Then open **`http://127.0.0.1:5050`** in your browser.

---

## User Instructions

### Step 1: Link Your WhatsApp Account
1. Look at the status badge in the top-right navbar.
2. If not yet linked, scan the QR code shown on screen from your phone:
   **WhatsApp → Menu / Settings → Linked Devices → Link a Device**.
3. Once linked, the badge turns green and shows **`WhatsApp is ready (+<your-number>)`**. Your session is cached locally in `wa-bridge/.wwebjs_auth/` so you don't have to scan every time.
4. If WhatsApp disconnects or the bridge stops, click **`Connect again...`** to trigger the connection sequence.

### Step 2: Add or Import Contacts (`1. Contact Manager`)
- **Single Contact:** Enter Honorific, Guest Name, WhatsApp Phone (10-digit Indian numbers automatically get `91` prepended), select existing groups via the dynamic group filter (or create new ones), and optionally add **Notes** (up to 150 characters).
- **Bulk CSV Import:** Click **Sample CSV** to download a template, fill it out using the columns below, and click **Upload & Import CSV**:
  ```csv
  Honorific,Name,Phone,Groups,Notes
  Shri,Rajesh Sharma & Family,9876543210,"Family,Bride Side,VIP","Arriving Friday evening; 4 guests"
  Smt.,Anita Verma,919812345678,"Friends,Bride Side","Vegetarian preference"
  ```
- **Export:** Click **Export All CSV** anytime to back up your guest directory.

### Step 3: Compose, Test & Launch (`2. Invitation Composer`)
1. Enter a **Campaign Name** (or load a previous/draft campaign from the **Saved Campaigns** dropdown).
2. Choose **Target Recipients**:
   - **Select by Groups:** Filter and check one or more groups (or leave unchecked for all contacts).
   - **Select Individuals:** Search by name, phone, group, or notes and check specific guests.
3. Write your **Invitation Message / Caption** using tokens (`{{Honorific}}`, `{{Name}}`, `{Spintax|Options}`) and optionally paste a local Windows file path for an image/PDF/video attachment.
4. Check the **Live WhatsApp Preview** and click **Send Test Now** to verify the message on your own WhatsApp first.
5. Choose one of the actions:
   - **Create & Launch / Schedule Campaign:** Starts immediately (if schedule is blank) or queues for the selected date/time.
   - **Save as Draft / Update Draft:** Saves all details so you can edit or send later.

### Step 4: Monitor, Retry & Reuse (`3. Campaign Monitor`)
- Select any campaign on the left to inspect live delivery progress, pacing status, and per-recipient logs.
- Use **Pause**, **Start / Resume**, or **Retry Failed** as needed.
- Click **Edit & Reuse** on any campaign to load all of its saved settings into the Composer, make edits, and send it as a brand-new campaign.

---

## Local Data & Privacy

- All contacts, campaign logs (`InviteApp/invites.db`), and WhatsApp Web session tokens (`wa-bridge/.wwebjs_auth/`) stay strictly on your local Windows 11 machine and are excluded from Git via `.gitignore`.
- Keep your PC powered on while a campaign is actively running; the built-in Windows 11 Sleep Guard keeps the system awake until sending completes.
