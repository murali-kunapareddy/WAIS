const express = require('express');
const qrcode = require('qrcode');
const fs = require('fs');
const { Client, LocalAuth, MessageMedia } = require('whatsapp-web.js');

const app = express();
app.use(express.json({ limit: '50mb' }));

let connectionState = 'DISCONNECTED';
let qrDataUrl = null;
let lastError = null;
let connectedUser = null;
let client = null;
let isConnecting = false;
let connectStartedAt = 0;

function resolveBrowserPath() {
  const candidates = [
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'
  ];
  return candidates.find((p) => fs.existsSync(p));
}

const executablePath = resolveBrowserPath();

function createWhatsAppClient() {
  const waClient = new Client({
    authStrategy: new LocalAuth({ dataPath: './.wwebjs_auth' }),
    puppeteer: {
      headless: true,
      ...(executablePath ? { executablePath } : {}),
      args: [
        '--no-sandbox',
        '--disable-setuid-sandbox',
        '--disable-dev-shm-usage',
        '--disable-accelerated-2d-canvas',
        '--no-first-run',
        '--disable-gpu'
      ]
    }
  });

  waClient.on('qr', async (qr) => {
    if (client !== waClient) return;
    try {
      qrDataUrl = await qrcode.toDataURL(qr);
      connectionState = 'QR_READY';
      lastError = null;
      isConnecting = false;
      console.log('[WA-Bridge] QR Code generated. Waiting for scan...');
    } catch (err) {
      console.error('[WA-Bridge] Failed to generate QR Data URL:', err);
      lastError = err.message;
      isConnecting = false;
    }
  });

  waClient.on('authenticated', () => {
    if (client !== waClient) return;
    connectionState = 'AUTHENTICATED';
    qrDataUrl = null;
    lastError = null;
    console.log('[WA-Bridge] Authenticated successfully.');
  });

  waClient.on('ready', () => {
    if (client !== waClient) return;
    connectionState = 'CONNECTED';
    qrDataUrl = null;
    lastError = null;
    isConnecting = false;
    connectedUser = waClient.info && waClient.info.wid ? waClient.info.wid.user : null;
    console.log(`[WA-Bridge] WhatsApp client is READY (${connectedUser || 'unknown'}).`);
  });

  waClient.on('auth_failure', (msg) => {
    if (client !== waClient) return;
    connectionState = 'AUTH_FAILURE';
    lastError = msg;
    isConnecting = false;
    console.error('[WA-Bridge] Authentication failure:', msg);
  });

  waClient.on('disconnected', (reason) => {
    if (client !== waClient) return;
    connectionState = 'DISCONNECTED';
    qrDataUrl = null;
    connectedUser = null;
    lastError = reason;
    isConnecting = false;
    console.warn('[WA-Bridge] Client disconnected:', reason);
  });

  return waClient;
}

async function connectWhatsApp({ force = false } = {}) {
  if (connectionState === 'CONNECTED' && !force) {
    return {
      success: true,
      alreadyConnected: true,
      state: connectionState,
      connected: true,
      user: connectedUser,
      message: 'WhatsApp is already connected and ready.'
    };
  }

  const elapsedMs = Date.now() - connectStartedAt;
  if (isConnecting && elapsedMs < 15000) {
    return {
      success: true,
      inProgress: true,
      state: connectionState,
      connected: false,
      message: 'WhatsApp connection sequence is already in progress.'
    };
  }

  isConnecting = true;
  connectStartedAt = Date.now();
  connectionState = 'INITIALIZING';
  qrDataUrl = null;
  lastError = null;
  connectedUser = null;

  const previousClient = client;
  client = null;

  if (previousClient) {
    try {
      console.log('[WA-Bridge] Cleaning up previous WhatsApp client before reconnecting...');
      await previousClient.destroy();
    } catch (destroyErr) {
      console.warn('[WA-Bridge] Ignored error while destroying previous client:', destroyErr.message);
    }
  }

  const newClient = createWhatsAppClient();
  client = newClient;

  console.log('[WA-Bridge] Starting WhatsApp connection sequence...');
  newClient
    .initialize()
    .catch((err) => {
      if (client !== newClient) return;
      connectionState = 'ERROR';
      lastError = err && err.message ? err.message : String(err);
      console.error('[WA-Bridge] Client initialization failed:', err);
    })
    .finally(() => {
      if (client === newClient) {
        isConnecting = false;
      }
    });

  return {
    success: true,
    started: true,
    state: connectionState,
    connected: false,
    message: 'WhatsApp connection sequence triggered.'
  };
}

/**
 * Rule 2: Strip all non-digits. If length is 10, auto-prepend 91 (India).
 */
function normalizePhone(phone) {
  let digits = String(phone || '').replace(/\D/g, '');
  if (digits.length === 10) {
    digits = '91' + digits;
  }
  return digits;
}

/**
 * Rule 6: Strip accidental surrounding quotes ("C:\path\card.jpg") before MessageMedia.fromFilePath().
 */
function cleanMediaPath(rawPath) {
  if (!rawPath || typeof rawPath !== 'string') return '';
  return rawPath.trim().replace(/^["']+|["']+$/g, '').trim();
}

// GET /status -> returns connection state and QR code Data URL
app.get('/status', (req, res) => {
  res.json({
    state: connectionState,
    connected: connectionState === 'CONNECTED',
    qr: qrDataUrl,
    qrDataUrl: qrDataUrl,
    user: connectedUser,
    error: lastError
  });
});

// POST /connect (alias /reconnect) -> triggers or retries WhatsApp connection sequence
async function handleConnectRequest(req, res) {
  try {
    const force = Boolean(req.body && req.body.force === true);
    const result = await connectWhatsApp({ force });
    return res.json(result);
  } catch (err) {
    console.error('[WA-Bridge] Error in /connect:', err);
    return res.status(500).json({
      success: false,
      state: connectionState,
      connected: connectionState === 'CONNECTED',
      error: err.message || 'Failed to trigger WhatsApp connection sequence.'
    });
  }
}

app.post('/connect', handleConnectRequest);
app.post('/reconnect', handleConnectRequest);

// POST /send -> sends text and/or media with caption using LID/JID resolution & safe typing
app.post('/send', async (req, res) => {
  try {
    if (!client || connectionState !== 'CONNECTED') {
      return res.status(503).json({
        success: false,
        error: `WhatsApp client is not connected (current state: ${connectionState})`
      });
    }

    const { phone, message, mediaPath, caption } = req.body || {};
    const textContent = message !== undefined ? message : (caption || '');

    const digits = normalizePhone(phone);
    if (!digits) {
      return res.status(400).json({
        success: false,
        error: 'A valid phone number is required.'
      });
    }

    // Rule 1: Never construct @c.us manually. Always resolve with client.getNumberId(digits)
    const numberId = await client.getNumberId(digits);
    if (!numberId || !numberId._serialized) {
      return res.status(404).json({
        success: false,
        error: `Number ${digits} is not registered on WhatsApp.`
      });
    }

    const targetJid = numberId._serialized;

    // Rule 3: Safe Typing Simulation wrapped in try/catch for brand-new chats
    try {
      const chat = await client.getChatById(targetJid);
      if (chat) {
        await chat.sendStateTyping();
        await new Promise((resolve) => setTimeout(resolve, 1500));
        await chat.clearState();
      }
    } catch (typingErr) {
      // Ignore Evaluation failed or missing thread errors on brand-new chats
      console.warn(`[WA-Bridge] Safe typing skipped for ${targetJid}: ${typingErr.message}`);
    }

    // Rule 6: Clean media path and send with caption if provided
    const cleanedPath = cleanMediaPath(mediaPath);
    let sentMessage;

    if (cleanedPath) {
      if (!fs.existsSync(cleanedPath)) {
        return res.status(400).json({
          success: false,
          error: `Media file not found at path: ${cleanedPath}`
        });
      }
      const media = MessageMedia.fromFilePath(cleanedPath);
      sentMessage = await client.sendMessage(targetJid, media, {
        caption: textContent || undefined
      });
    } else {
      if (!textContent) {
        return res.status(400).json({
          success: false,
          error: 'Either message text or mediaPath must be provided.'
        });
      }
      sentMessage = await client.sendMessage(targetJid, textContent);
    }

    return res.json({
      success: true,
      jid: targetJid,
      messageId: sentMessage && sentMessage.id ? sentMessage.id._serialized : null
    });
  } catch (err) {
    console.error('[WA-Bridge] Error in /send:', err);
    return res.status(500).json({
      success: false,
      error: err.message || 'Unknown error occurred while sending WhatsApp message.'
    });
  }
});

const PORT = 3001;
const HOST = '127.0.0.1';

app.listen(PORT, HOST, () => {
  console.log(`[WA-Bridge] Listening on http://${HOST}:${PORT}`);
  connectWhatsApp().catch((err) => {
    connectionState = 'ERROR';
    lastError = err.message;
    isConnecting = false;
    console.error('[WA-Bridge] Initial connection failed:', err);
  });
});
