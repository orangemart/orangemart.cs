

# Orangemart (v0.5.0)

**Orangemart** is a [Rust plugin](https://umod.org/) that bridges your game server with the real-world Bitcoin Lightning Network. It allows players to buy in-game items, send currency to other players (withdrawals), and purchase VIP status using instant Lightning payments via an LNbits backend.

## ⚡ New in v0.5.0

  * **Instant Transactions:** Now uses **WebSockets** for real-time payment detection (no more waiting for polling).
  * **Smart Expiry:** Discord invoices now automatically update to show "EXPIRED" status when timed out.
  * **Protection Suite:** Added rate limiting, max transaction caps, and overflow protection to prevent abuse.
  * **Crash Prevention:** Fully thread-safe item handling to prevent server stalls.

-----

## Features

  * **Real-Time Deposits (`/buyblood`):** Players generate a QR code (linked to Discord) to buy in-game currency. Payments are detected instantly via WebSockets.
  * **Withdrawals (`/sendblood`):** Players can "burn" in-game items to send real Sats to any Lightning Address (e.g., Wallet of Satoshi, Strike, CashApp).
  * **VIP Automation:** Purchase VIP status / permissions automatically with Bitcoin.
  * **Discord Integration:** Sends beautiful embed invoices to a designated Discord channel.
  * **Anti-Abuse:** Configurable cooldowns, per-player transaction limits, and pending invoice caps.

-----

## Commands

### Player Commands

  * **`/buyblood <amount>`**
    Generates a Lightning invoice to purchase in-game currency.

      * *Example:* `/buyblood 100`

  * **`/sendblood <amount> <lightning_address>`**
    Destroys in-game currency and sends real Bitcoin to the specified Lightning Address.

      * *Example:* `/sendblood 50 user@walletofsatoshi.com`

  * **`/buyvip`**
    Generates an invoice to purchase VIP status (runs the configured console command upon success).

-----

## Configuration

The configuration file allows you to set connection details, pricing, and security limits.

### Default Configuration (`oxide/config/Orangemart.json`)

```json
{
  "Commands": {
    "BuyCurrencyCommandName": "buyblood",
    "SendCurrencyCommandName": "sendblood",
    "BuyVipCommandName": "buyvip"
  },
  "CurrencySettings": {
    "CurrencyItemID": 1776460938,
    "CurrencyName": "blood",
    "CurrencySkinID": 0,
    "PricePerCurrencyUnit": 1,
    "SatsPerCurrencyUnit": 1,
    "MaxPurchaseAmount": 10000,
    "MaxSendAmount": 10000,
    "CommandCooldownSeconds": 0,
    "MaxPendingInvoicesPerPlayer": 1
  },
  "Discord": {
    "DiscordChannelName": "mart",
    "DiscordWebhookUrl": "https://discord.com/api/webhooks/your_webhook_url"
  },
  "InvoiceSettings": {
    "BlacklistedDomains": [
      "example.com",
      "blacklisted.net"
    ],
    "WhitelistedDomains": [],
    "CheckIntervalSeconds": 10,
    "InvoiceTimeoutSeconds": 300,
    "LNbitsApiKey": "your-lnbits-admin-api-key",
    "LNbitsBaseUrl": "https://your-lnbits-instance.com",
    "MaxRetries": 25,
    "UseWebSockets": true,
    "WebSocketReconnectDelay": 5
  },
  "VIPSettings": {
    "VipCommand": "oxide.usergroup add {steamid} vip",
    "VipPrice": 1000
  }
}
```

### Key Settings Explained

#### 🛡️ Protection Settings (New)

  * **`MaxPurchaseAmount`**: The maximum amount of items a player can buy in one go.
  * **`MaxSendAmount`**: The maximum amount a player can withdraw/send in one go.
  * **`CommandCooldownSeconds`**: Time (in seconds) a player must wait between commands. Set to `0` to disable.
  * **`MaxPendingInvoicesPerPlayer`**: Prevents players from spamming the server with unpaid invoices.

#### ⚡ Invoice Settings

  * **`UseWebSockets`**: Set to `true` for instant payment detection. If `false`, it falls back to slower polling.
  * **`LNbitsBaseUrl`**: Your LNbits server URL (e.g., `https://legend.lnbits.com`).
  * **`LNbitsApiKey`**: The **Admin Key** from your LNbits wallet.

#### 👑 VIP Settings

  * **`VipCommand`**: The console command to run when payment is successful. Supports placeholders:
      * `{player}` - Player Name
      * `{steamid}` - Steam ID (UserID)

-----

## Installation

1.  **Prerequisites:** You must have an [LNbits](https://github.com/lnbits/lnbits) wallet instance running (or use a hosted one like https://www.google.com/search?q=legend.lnbits.com).
2.  **Download:** Place `Orangemart.cs` into your `oxide/plugins` folder.
3.  **Config:** Edit `oxide/config/Orangemart.json` and add your **LNbits API Key** and **Discord Webhook URL**.
4.  **Reload:** Run `o.reload Orangemart`.

-----

## Permissions

  * **`orangemart.buycurrency`** - Allows players to use `/buyblood`
  * **`orangemart.sendcurrency`** - Allows players to use `/sendblood`
  * **`orangemart.buyvip`** - Allows players to use `/buyvip`

-----

## Troubleshooting

  * **Invoices not appearing in Discord?**
    Check that your `DiscordWebhookUrl` is correct and that the plugin has loaded without errors in the server console.
  * **Payments not registering instantly?**
    Ensure `UseWebSockets` is set to `true` and that your server can connect to your LNbits instance via port 443 (HTTPS/WSS).
  * **"Inventory Full" messages?**
    If a player pays while their inventory is full, the item will drop at their feet.

-----

