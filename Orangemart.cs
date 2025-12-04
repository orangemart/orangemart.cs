using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Libraries;

namespace Oxide.Plugins
{
    [Info("Orangemart", "RustySats Orangemart", "0.5.0")]
    [Description("Allows players to buy and sell in-game units and VIP status using Bitcoin Lightning Network payments via LNbits with WebSocket support and comprehensive protection features")]
    public class Orangemart : CovalencePlugin
    {
        // Configuration sections and keys
        private static class ConfigSections
        {
            public const string Commands = "Commands";
            public const string CurrencySettings = "CurrencySettings";
            public const string Discord = "Discord";
            public const string InvoiceSettings = "InvoiceSettings";
            public const string VIPSettings = "VIPSettings";
        }

        private static class ConfigKeys
        {
            // Commands
            public const string BuyCurrencyCommandName = "BuyCurrencyCommandName";
            public const string SendCurrencyCommandName = "SendCurrencyCommandName";
            public const string BuyVipCommandName = "BuyVipCommandName";

            // CurrencySettings
            public const string CurrencyItemID = "CurrencyItemID";
            public const string CurrencyName = "CurrencyName";
            public const string CurrencySkinID = "CurrencySkinID";
            public const string PricePerCurrencyUnit = "PricePerCurrencyUnit";
            public const string SatsPerCurrencyUnit = "SatsPerCurrencyUnit";
            
            // Protection Settings
            public const string MaxPurchaseAmount = "MaxPurchaseAmount";
            public const string MaxSendAmount = "MaxSendAmount";
            public const string CommandCooldownSeconds = "CommandCooldownSeconds";
            public const string MaxPendingInvoicesPerPlayer = "MaxPendingInvoicesPerPlayer";

            // Discord
            public const string DiscordChannelName = "DiscordChannelName";
            public const string DiscordWebhookUrl = "DiscordWebhookUrl";

            // InvoiceSettings
            public const string BlacklistedDomains = "BlacklistedDomains";
            public const string WhitelistedDomains = "WhitelistedDomains";
            public const string CheckIntervalSeconds = "CheckIntervalSeconds";
            public const string InvoiceTimeoutSeconds = "InvoiceTimeoutSeconds";
            public const string LNbitsApiKey = "LNbitsApiKey";
            public const string LNbitsBaseUrl = "LNbitsBaseUrl";
            public const string MaxRetries = "MaxRetries";
            public const string UseWebSockets = "UseWebSockets";
            public const string WebSocketReconnectDelay = "WebSocketReconnectDelay";

            // VIPSettings
            public const string VipPrice = "VipPrice";
            public const string VipCommand = "VipCommand";
        }

        // Configuration variables
        private int currencyItemID;
        private string buyCurrencyCommandName;
        private string sendCurrencyCommandName;
        private string buyVipCommandName;
        private int vipPrice;
        private string vipCommand;
        private string currencyName;
        private int satsPerCurrencyUnit;
        private int pricePerCurrencyUnit;
        private string discordChannelName;
        private ulong currencySkinID;
        private int checkIntervalSeconds;
        private int invoiceTimeoutSeconds;
        private int maxRetries;
        private bool useWebSockets;
        private int webSocketReconnectDelay;
        private List<string> blacklistedDomains = new List<string>();
        private List<string> whitelistedDomains = new List<string>();
        
        // Protection and rate limiting variables
        private int maxPurchaseAmount;
        private int maxSendAmount;
        private int commandCooldownSeconds;
        private int maxPendingInvoicesPerPlayer;
        private Dictionary<string, DateTime> lastCommandTime = new Dictionary<string, DateTime>();

        private const string SellLogFile = "Orangemart/send_bitcoin.json";
        private const string BuyInvoiceLogFile = "Orangemart/buy_invoices.json";
        private LNbitsConfig config;
        private List<PendingInvoice> pendingInvoices = new List<PendingInvoice>();
        private Dictionary<string, int> retryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        // WebSocket tracking
        private Dictionary<string, WebSocketConnection> activeWebSockets = new Dictionary<string, WebSocketConnection>();
        private readonly object webSocketLock = new object();

        // Transaction status constants
        private static class TransactionStatus
        {
            public const string INITIATED = "INITIATED";
            public const string PROCESSING = "PROCESSING";
            public const string COMPLETED = "COMPLETED";
            public const string FAILED = "FAILED";
            public const string EXPIRED = "EXPIRED";
            public const string REFUNDED = "REFUNDED";
        }

        // WebSocket connection wrapper
        private class WebSocketConnection
        {
            public ClientWebSocket WebSocket { get; set; }
            public CancellationTokenSource CancellationTokenSource { get; set; }
            public string InvoiceKey { get; set; }
            public PendingInvoice Invoice { get; set; }
            public DateTime ConnectedAt { get; set; }
            public int ReconnectAttempts { get; set; }
            public Task ListenTask { get; set; }
        }

        // WebSocket response structure
        private class WebSocketPaymentUpdate
        {
            [JsonProperty("balance")]
            public long Balance { get; set; }
            
            [JsonProperty("payment")]
            public WebSocketPayment Payment { get; set; }
        }

        private class WebSocketPayment
        {
            [JsonProperty("checking_id")]
            public string CheckingId { get; set; }
            
            [JsonProperty("pending")]
            public bool Pending { get; set; }
            
            [JsonProperty("amount")]
            public long Amount { get; set; }
            
            [JsonProperty("payment_hash")]
            public string PaymentHash { get; set; }
            
            [JsonProperty("preimage")]
            public string Preimage { get; set; }
        }

        // LNbits Configuration
        private class LNbitsConfig
        {
            public string BaseUrl { get; set; }
            public string ApiKey { get; set; }
            public string DiscordWebhookUrl { get; set; }
            public string WebSocketUrl { get; set; }

            public static LNbitsConfig ParseLNbitsConnection(string baseUrl, string apiKey, string discordWebhookUrl)
            {
                var trimmedBaseUrl = baseUrl.TrimEnd('/');
                if (!Uri.IsWellFormedUriString(trimmedBaseUrl, UriKind.Absolute))
                    throw new Exception("Invalid base URL in connection string.");

                // Convert HTTP URL to WebSocket URL
                var wsUrl = trimmedBaseUrl.Replace("https://", "wss://").Replace("http://", "ws://");

                return new LNbitsConfig
                {
                    BaseUrl = trimmedBaseUrl,
                    ApiKey = apiKey,
                    DiscordWebhookUrl = discordWebhookUrl,
                    WebSocketUrl = wsUrl
                };
            }
        }

        // Invoice and Payment Classes
        private class InvoiceResponse
        {
            [JsonProperty("bolt11")]
            public string PaymentRequest { get; set; }

            [JsonProperty("payment_hash")]
            public string PaymentHash { get; set; }
        }

        // Wrapper class for LNbits v1 responses
        private class InvoiceResponseWrapper
        {
            [JsonProperty("data")]
            public InvoiceResponse Data { get; set; }
        }

        private class SellInvoiceLogEntry
        {
            public string TransactionId { get; set; }
            public string SteamID { get; set; }
            public string LightningAddress { get; set; }
            public string Status { get; set; }
            public bool Success { get; set; }
            public int SatsAmount { get; set; }
            public string PaymentHash { get; set; }
            public bool CurrencyReturned { get; set; }
            public DateTime Timestamp { get; set; }
            public DateTime? CompletedTimestamp { get; set; }
            public int RetryCount { get; set; }
            public string FailureReason { get; set; }
        }

        private class BuyInvoiceLogEntry
        {
            public string TransactionId { get; set; }
            public string SteamID { get; set; }
            public string InvoiceID { get; set; }
            public string Status { get; set; }
            public bool IsPaid { get; set; }
            public DateTime Timestamp { get; set; }
            public DateTime? CompletedTimestamp { get; set; }
            public int Amount { get; set; }
            public bool CurrencyGiven { get; set; }
            public bool VipGranted { get; set; }
            public int RetryCount { get; set; }
            public string PurchaseType { get; set; }
        }

        private class PendingInvoice
        {
            public string TransactionId { get; set; }
            public string RHash { get; set; }
            public IPlayer Player { get; set; }
            public int Amount { get; set; }
            public string Memo { get; set; }
            public DateTime CreatedAt { get; set; }
            public PurchaseType Type { get; set; }
            // NEW: Store the Discord Message ID
            public string DiscordMessageId { get; set; } 
        }

        private enum PurchaseType
        {
            Currency,
            Vip,
            SendBitcoin
        }

        private class PaymentStatusResponse
        {
            [JsonProperty("paid")]
            public bool Paid { get; set; }

            [JsonProperty("preimage")]
            public string Preimage { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                bool configChanged = false;

                // Parse LNbits connection settings
                config = LNbitsConfig.ParseLNbitsConnection(
                    GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.LNbitsBaseUrl, "https://your-lnbits-instance.com", ref configChanged),
                    GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.LNbitsApiKey, "your-lnbits-admin-api-key", ref configChanged),
                    GetConfigValue(ConfigSections.Discord, ConfigKeys.DiscordWebhookUrl, "https://discord.com/api/webhooks/your_webhook_url", ref configChanged)
                );

                // Parse Currency Settings
                currencyItemID = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.CurrencyItemID, 1776460938, ref configChanged);
                currencyName = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.CurrencyName, "blood", ref configChanged);
                satsPerCurrencyUnit = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.SatsPerCurrencyUnit, 1, ref configChanged);
                pricePerCurrencyUnit = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.PricePerCurrencyUnit, 1, ref configChanged);
                currencySkinID = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.CurrencySkinID, 0UL, ref configChanged);

                // Parse Protection Settings
                maxPurchaseAmount = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.MaxPurchaseAmount, 10000, ref configChanged);
                maxSendAmount = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.MaxSendAmount, 10000, ref configChanged);
                commandCooldownSeconds = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.CommandCooldownSeconds, 0, ref configChanged);
                maxPendingInvoicesPerPlayer = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.MaxPendingInvoicesPerPlayer, 1, ref configChanged);

                // Ensure non-negative values
                if (maxPurchaseAmount < 0) maxPurchaseAmount = 0;
                if (maxSendAmount < 0) maxSendAmount = 0;
                if (commandCooldownSeconds < 0) commandCooldownSeconds = 0;
                if (maxPendingInvoicesPerPlayer < 0) maxPendingInvoicesPerPlayer = 0;

                // Parse Command Names
                buyCurrencyCommandName = GetConfigValue(ConfigSections.Commands, ConfigKeys.BuyCurrencyCommandName, "buyblood", ref configChanged);
                sendCurrencyCommandName = GetConfigValue(ConfigSections.Commands, ConfigKeys.SendCurrencyCommandName, "sendblood", ref configChanged);
                buyVipCommandName = GetConfigValue(ConfigSections.Commands, ConfigKeys.BuyVipCommandName, "buyvip", ref configChanged);

                // Parse VIP Settings
                vipPrice = GetConfigValue(ConfigSections.VIPSettings, ConfigKeys.VipPrice, 1000, ref configChanged);
                vipCommand = GetConfigValue(ConfigSections.VIPSettings, ConfigKeys.VipCommand, "oxide.usergroup add {player} vip", ref configChanged);

                // Parse Discord Settings
                discordChannelName = GetConfigValue(ConfigSections.Discord, ConfigKeys.DiscordChannelName, "mart", ref configChanged);

                // Parse Invoice Settings
                checkIntervalSeconds = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.CheckIntervalSeconds, 10, ref configChanged);
                invoiceTimeoutSeconds = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.InvoiceTimeoutSeconds, 300, ref configChanged);
                maxRetries = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.MaxRetries, 25, ref configChanged);
                useWebSockets = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.UseWebSockets, true, ref configChanged);
                webSocketReconnectDelay = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.WebSocketReconnectDelay, 5, ref configChanged);

                blacklistedDomains = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.BlacklistedDomains, new List<string> { "example.com", "blacklisted.net" }, ref configChanged)
                    .Select(d => d.ToLower()).ToList();

                whitelistedDomains = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.WhitelistedDomains, new List<string>(), ref configChanged)
                    .Select(d => d.ToLower()).ToList();

                if (configChanged)
                {
                    SaveConfig();
                }

                Puts($"Protection Settings: MaxPurchase={maxPurchaseAmount}, MaxSend={maxSendAmount}, Cooldown={commandCooldownSeconds}s, MaxPending={maxPendingInvoicesPerPlayer}");
            }
            catch (Exception ex)
            {
                PrintError($"Failed to load configuration: {ex.Message}");
            }
        }

        private T GetConfigValue<T>(string section, string key, T defaultValue, ref bool configChanged)
        {
            if (!(Config[section] is Dictionary<string, object> data))
            {
                data = new Dictionary<string, object>();
                Config[section] = data;
                configChanged = true;
            }

            if (!data.TryGetValue(key, out var value))
            {
                value = defaultValue;
                data[key] = value;
                configChanged = true;
            }

            try
            {
                if (value is T tValue) return tValue;
                if (typeof(T) == typeof(List<string>))
                {
                    if (value is IEnumerable<object> enumerable)
                        return (T)(object)enumerable.Select(item => item.ToString()).ToList();
                    return (T)(object)new List<string> { value.ToString() };
                }
                if (typeof(T) == typeof(ulong))
                {
                    return (T)(object)Convert.ToUInt64(value);
                }
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                data[key] = defaultValue;
                configChanged = true;
                return defaultValue;
            }
        }

        protected override void LoadDefaultConfig()
        {
            Config[ConfigSections.Commands] = new Dictionary<string, object>
            {
                [ConfigKeys.BuyCurrencyCommandName] = "buyblood",
                [ConfigKeys.BuyVipCommandName] = "buyvip",
                [ConfigKeys.SendCurrencyCommandName] = "sendblood"
            };

            Config[ConfigSections.CurrencySettings] = new Dictionary<string, object>
            {
                [ConfigKeys.CurrencyItemID] = 1776460938,
                [ConfigKeys.CurrencyName] = "blood",
                [ConfigKeys.CurrencySkinID] = 0UL,
                [ConfigKeys.PricePerCurrencyUnit] = 1,
                [ConfigKeys.SatsPerCurrencyUnit] = 1,
                [ConfigKeys.MaxPurchaseAmount] = 10000,
                [ConfigKeys.MaxSendAmount] = 10000,
                [ConfigKeys.CommandCooldownSeconds] = 0,
                [ConfigKeys.MaxPendingInvoicesPerPlayer] = 1
            };

            Config[ConfigSections.Discord] = new Dictionary<string, object>
            {
                [ConfigKeys.DiscordChannelName] = "mart",
                [ConfigKeys.DiscordWebhookUrl] = "https://discord.com/api/webhooks/your_webhook_url"
            };

            Config[ConfigSections.InvoiceSettings] = new Dictionary<string, object>
            {
                [ConfigKeys.BlacklistedDomains] = new List<string> { "example.com", "blacklisted.net" },
                [ConfigKeys.WhitelistedDomains] = new List<string>(),
                [ConfigKeys.CheckIntervalSeconds] = 10,
                [ConfigKeys.InvoiceTimeoutSeconds] = 300,
                [ConfigKeys.LNbitsApiKey] = "your-lnbits-admin-api-key",
                [ConfigKeys.LNbitsBaseUrl] = "https://your-lnbits-instance.com",
                [ConfigKeys.MaxRetries] = 25,
                [ConfigKeys.UseWebSockets] = true,
                [ConfigKeys.WebSocketReconnectDelay] = 5
            };

            Config[ConfigSections.VIPSettings] = new Dictionary<string, object>
            {
                [ConfigKeys.VipCommand] = "oxide.usergroup add {steamid} vip",
                [ConfigKeys.VipPrice] = 1000
            };
        }

        private void Init()
        {
            permission.RegisterPermission("orangemart.buycurrency", this);
            permission.RegisterPermission("orangemart.sendcurrency", this);
            permission.RegisterPermission("orangemart.buyvip", this);
        }

        private void OnServerInitialized()
        {
            if (config == null)
            {
                PrintError("Plugin configuration is not properly set up.");
                return;
            }

            AddCovalenceCommand(buyCurrencyCommandName, nameof(CmdBuyCurrency), "orangemart.buycurrency");
            AddCovalenceCommand(sendCurrencyCommandName, nameof(CmdSendCurrency), "orangemart.sendcurrency");
            AddCovalenceCommand(buyVipCommandName, nameof(CmdBuyVip), "orangemart.buyvip");

            RecoverInterruptedTransactions();

            timer.Every(checkIntervalSeconds, CheckPendingInvoices);
            timer.Every(300f, CleanupOldCooldowns);

            Puts($"Orangemart initialized. WebSockets: {(useWebSockets ? "Enabled" : "Disabled")}");
        }

        private void Unload()
        {
            CleanupAllWebSockets();
            pendingInvoices.Clear();
            retryCounts.Clear();
            lastCommandTime.Clear();
        }

        // Protection Methods
        private bool IsOnCooldown(IPlayer player, string commandType)
        {
            if (commandCooldownSeconds <= 0) return false;
            
            string key = $"{GetPlayerId(player)}:{commandType}";
            
            if (lastCommandTime.TryGetValue(key, out DateTime lastTime))
            {
                double secondsSince = (DateTime.UtcNow - lastTime).TotalSeconds;
                if (secondsSince < commandCooldownSeconds)
                {
                    double remaining = commandCooldownSeconds - secondsSince;
                    player.Reply(Lang("CommandOnCooldown", player.Id, commandType, Math.Ceiling(remaining)));
                    return true;
                }
            }
            
            lastCommandTime[key] = DateTime.UtcNow;
            return false;
        }

        private bool HasTooManyPendingInvoices(IPlayer player)
        {
            if (maxPendingInvoicesPerPlayer == 0) return false;
            
            string playerId = GetPlayerId(player);
            int pendingCount = pendingInvoices.Count(inv => GetPlayerId(inv.Player) == playerId);
            
            if (pendingCount >= maxPendingInvoicesPerPlayer)
            {
                player.Reply(Lang("TooManyPendingInvoices", player.Id, pendingCount, maxPendingInvoicesPerPlayer));
                return true;
            }
            
            return false;
        }

        private bool ValidatePurchaseAmount(IPlayer player, int amount, out int safeSats)
        {
            safeSats = 0;
            if (amount <= 0)
            {
                player.Reply(Lang("InvalidAmount", player.Id));
                return false;
            }
            if (maxPurchaseAmount > 0 && amount > maxPurchaseAmount)
            {
                player.Reply(Lang("AmountTooLarge", player.Id, amount, maxPurchaseAmount, currencyName));
                return false;
            }
            
            long amountSatsLong = (long)amount * pricePerCurrencyUnit;
            if (amountSatsLong > int.MaxValue)
            {
                player.Reply(Lang("AmountCausesOverflow", player.Id));
                return false;
            }
            
            safeSats = (int)amountSatsLong;
            return true;
        }

        private bool ValidateSendAmount(IPlayer player, int amount, out int safeSats)
        {
            safeSats = 0;
            if (amount <= 0)
            {
                player.Reply(Lang("InvalidAmount", player.Id));
                return false;
            }
            if (maxSendAmount > 0 && amount > maxSendAmount)
            {
                player.Reply(Lang("SendAmountTooLarge", player.Id, amount, maxSendAmount, currencyName));
                return false;
            }
            
            long amountSatsLong = (long)amount * satsPerCurrencyUnit;
            if (amountSatsLong > int.MaxValue)
            {
                player.Reply(Lang("AmountCausesOverflow", player.Id));
                return false;
            }
            
            safeSats = (int)amountSatsLong;
            return true;
        }

        private bool ValidateVipPrice(IPlayer player, out int safeSats)
        {
            safeSats = 0;
            if (vipPrice > int.MaxValue)
            {
                player.Reply(Lang("VipPriceTooHigh", player.Id));
                return false;
            }
            safeSats = vipPrice;
            return true;
        }

        private void CleanupOldCooldowns()
        {
            var expiredKeys = lastCommandTime
                .Where(kvp => (DateTime.UtcNow - kvp.Value).TotalSeconds > commandCooldownSeconds * 2)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in expiredKeys)
                lastCommandTime.Remove(key);
        }

        private void CleanupAllWebSockets()
        {
            lock (webSocketLock)
            {
                foreach (var kvp in activeWebSockets)
                {
                    try
                    {
                        kvp.Value.CancellationTokenSource?.Cancel();
                        kvp.Value.WebSocket?.Dispose();
                    }
                    catch { }
                }
                activeWebSockets.Clear();
            }
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["UsageSendCurrency"] = "Usage: /{0} <amount> <lightning_address>",
                ["NeedMoreCurrency"] = "You need more {0}. You currently have {1}.",
                ["FailedToReserveCurrency"] = "Failed to reserve currency. Please try again.",
                ["FailedToQueryLightningAddress"] = "Failed to query Lightning address for an invoice.",
                ["FailedToAuthenticate"] = "Failed to authenticate with LNbits.",
                ["InvoiceCreatedCheckDiscord"] = "Invoice created! Please check the #{0} channel on Discord.",
                ["FailedToCreateInvoice"] = "Failed to create an invoice. Please try again later.",
                ["FailedToProcessPayment"] = "Failed to process payment. Please try again later.",
                ["CurrencySentSuccess"] = "You have successfully sent {0} {1}!",
                ["PurchaseSuccess"] = "You have successfully purchased {0} {1}!",
                ["PurchaseVipSuccess"] = "You have successfully purchased VIP status!",
                ["InvalidCommandUsage"] = "Usage: /{0} <amount>",
                ["NoPermission"] = "You do not have permission to use this command.",
                ["FailedToFindBasePlayer"] = "Failed to find base player object for player {0}.",
                ["FailedToCreateCurrencyItem"] = "Failed to create {0} item for player {1}.",
                ["AddedToVipGroup"] = "Player {0} added to VIP group '{1}'.",
                ["InvoiceExpired"] = "Your invoice for {0} sats has expired. Please try again.",
                ["BlacklistedDomain"] = "The domain '{0}' is currently blacklisted.",
                ["NotWhitelistedDomain"] = "The domain '{0}' is not whitelisted. Allowed: {1}.",
                ["InvalidLightningAddress"] = "The Lightning Address provided is invalid.",
                ["PaymentProcessing"] = "Your payment is being processed...",
                ["TransactionInitiated"] = "Transaction initiated. Processing your payment...",
                ["InvalidAmount"] = "Invalid amount. Please enter a positive number.",
                ["AmountTooLarge"] = "Amount {0} exceeds maximum limit of {1} {2}.",
                ["SendAmountTooLarge"] = "Send amount {0} exceeds maximum limit of {1} {2}.",
                ["AmountCausesOverflow"] = "Amount too large. Please use a smaller amount.",
                ["CommandOnCooldown"] = "Command '{0}' is on cooldown. Wait {1}s.",
                ["TooManyPendingInvoices"] = "You have {0} pending invoices (max: {1}).",
                ["VipPriceTooHigh"] = "VIP price is configured too high.",
                ["ProtectionLimits"] = "Orangemart Limits: Purchase max {0}, Send max {1}, Cooldown {2}s"
            }, this);
        }

        private string Lang(string key, string userId = null, params object[] args)
        {
            return string.Format(lang.GetMessage(key, this, userId), args);
        }

        private string GenerateTransactionId()
        {
            return $"{DateTime.UtcNow.Ticks}-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        }

        // WebSocket connection management
        private async Task ConnectWebSocket(PendingInvoice invoice)
        {
            if (!useWebSockets) return;

            // Only use WebSockets for Incoming payments (Buy/VIP)
            // Outgoing payments (Send) are handled via immediate check + polling
            if (invoice.Type == PurchaseType.SendBitcoin) return;

            var wsConnection = new WebSocketConnection
            {
                WebSocket = new ClientWebSocket(),
                CancellationTokenSource = new CancellationTokenSource(),
                InvoiceKey = invoice.RHash,
                Invoice = invoice,
                ConnectedAt = DateTime.UtcNow,
                ReconnectAttempts = 0
            };

            wsConnection.WebSocket.Options.SetRequestHeader("X-Api-Key", config.ApiKey);

            lock (webSocketLock)
            {
                if (activeWebSockets.ContainsKey(invoice.RHash))
                {
                    var existing = activeWebSockets[invoice.RHash];
                    existing.CancellationTokenSource?.Cancel();
                    existing.WebSocket?.Dispose();
                }
                activeWebSockets[invoice.RHash] = wsConnection;
            }

            try
            {
                var wsUrl = $"{config.WebSocketUrl}/api/v1/ws/{invoice.RHash}";
                await wsConnection.WebSocket.ConnectAsync(new Uri(wsUrl), wsConnection.CancellationTokenSource.Token);
                
                wsConnection.ListenTask = Task.Run(async () => await ListenToWebSocket(wsConnection), wsConnection.CancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                PrintError($"Failed to connect WebSocket for invoice {invoice.RHash}: {ex.Message}");
                lock (webSocketLock) { activeWebSockets.Remove(invoice.RHash); }
            }
        }

        private async Task ListenToWebSocket(WebSocketConnection connection)
        {
            var buffer = new ArraySegment<byte>(new byte[4096]);
            var messageBuilder = new StringBuilder();

            try
            {
                while (connection.WebSocket.State == WebSocketState.Open && !connection.CancellationTokenSource.Token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    messageBuilder.Clear();

                    do
                    {
                        result = await connection.WebSocket.ReceiveAsync(buffer, connection.CancellationTokenSource.Token);
                        if (result.MessageType == WebSocketMessageType.Text)
                            messageBuilder.Append(Encoding.UTF8.GetString(buffer.Array, 0, result.Count));
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await connection.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            break;
                        }
                    }
                    while (!result.EndOfMessage);

                    if (messageBuilder.Length > 0)
                        ProcessWebSocketMessage(connection, messageBuilder.ToString());
                }
            }
            catch { }
            finally
            {
                lock (webSocketLock)
                {
                    if (activeWebSockets.ContainsKey(connection.InvoiceKey))
                        activeWebSockets.Remove(connection.InvoiceKey);
                }
                connection.WebSocket?.Dispose();
            }
        }

        private void ProcessWebSocketMessage(WebSocketConnection connection, string message)
        {
            try
            {
                bool confirmed = false;

                // Try Simple Format
                try
                {
                    var simpleUpdate = JsonConvert.DeserializeObject<Dictionary<string, object>>(message);
                    if (simpleUpdate != null && simpleUpdate.ContainsKey("pending") && simpleUpdate.ContainsKey("status"))
                    {
                        bool isPending = Convert.ToBoolean(simpleUpdate["pending"]);
                        string status = simpleUpdate["status"]?.ToString();
                        if (!isPending && status == "success") confirmed = true;
                    }
                }
                catch { }
                
                // Try Complex Format
                if (!confirmed)
                {
                    try
                    {
                        var update = JsonConvert.DeserializeObject<WebSocketPaymentUpdate>(message);
                        if (update?.Payment != null)
                        {
                            if (!update.Payment.Pending && !string.IsNullOrEmpty(update.Payment.Preimage)) confirmed = true;
                            else if (!update.Payment.Pending && update.Payment.PaymentHash?.ToLower() == connection.InvoiceKey.ToLower()) confirmed = true;
                        }
                    }
                    catch { }
                }

                if (confirmed)
                {
                    Puts($"[WebSocket] Payment confirmed for {connection.InvoiceKey}");
                    
                    // CRITICAL FIX: Dispatch to Main Thread
                    Interface.Oxide.NextTick(() => {
                        ProcessPaymentConfirmation(connection.Invoice);
                    });
                    
                    connection.CancellationTokenSource?.Cancel();
                }
            }
            catch (Exception ex)
            {
                PrintError($"Error processing WebSocket message: {ex.Message}");
            }
        }

        private void ProcessPaymentConfirmation(PendingInvoice invoice)
        {
            // CRITICAL: Prevent Race Conditions / Double Processing
            if (!pendingInvoices.Contains(invoice)) return;

            pendingInvoices.Remove(invoice);
            
            Puts($"[ProcessPayment] Processing payment confirmation for {invoice.RHash}, Type: {invoice.Type}");

            switch (invoice.Type)
            {
                case PurchaseType.Currency:
                    RewardPlayer(invoice.Player, invoice.Amount);
                    UpdateBuyTransactionStatus(invoice.TransactionId, TransactionStatus.COMPLETED, true);
                    break;
                case PurchaseType.Vip:
                    GrantVip(invoice.Player);
                    UpdateBuyTransactionStatus(invoice.TransactionId, TransactionStatus.COMPLETED, true);
                    break;
                case PurchaseType.SendBitcoin:
                    invoice.Player.Reply(Lang("CurrencySentSuccess", invoice.Player.Id, invoice.Amount / satsPerCurrencyUnit, currencyName));
                    UpdateSellTransactionStatus(invoice.TransactionId, TransactionStatus.COMPLETED, true);
                    break;
            }

            retryCounts.Remove(invoice.RHash);
            
            lock (webSocketLock)
            {
                if (activeWebSockets.ContainsKey(invoice.RHash))
                {
                    activeWebSockets[invoice.RHash].CancellationTokenSource?.Cancel();
                    activeWebSockets.Remove(invoice.RHash);
                }
            }
        }

        private void RecoverInterruptedTransactions()
        {
            Puts("Checking for interrupted transactions...");

            var sellLogs = LoadSellLogData();
            foreach (var log in sellLogs.Where(l => l.Status == TransactionStatus.INITIATED || l.Status == TransactionStatus.PROCESSING))
            {
                if (!string.IsNullOrEmpty(log.PaymentHash))
                {
                    CheckInvoicePaid(log.PaymentHash, isPaid =>
                    {
                        if (isPaid) UpdateSellTransactionStatus(log.TransactionId, TransactionStatus.COMPLETED, true);
                        else UpdateSellTransactionStatus(log.TransactionId, TransactionStatus.FAILED, false, "Server interrupted");
                    });
                }
                else
                {
                    UpdateSellTransactionStatus(log.TransactionId, TransactionStatus.FAILED, false, "Interrupted before init");
                }
            }

            var buyLogs = LoadBuyLogData();
            foreach (var log in buyLogs.Where(l => l.Status == TransactionStatus.INITIATED || l.Status == TransactionStatus.PROCESSING))
            {
                if (!string.IsNullOrEmpty(log.InvoiceID))
                {
                    CheckInvoicePaid(log.InvoiceID, isPaid =>
                    {
                        if (isPaid) UpdateBuyTransactionStatus(log.TransactionId, TransactionStatus.COMPLETED, true);
                        else UpdateBuyTransactionStatus(log.TransactionId, TransactionStatus.EXPIRED, false);
                    });
                }
            }
        }

        // Protected CmdBuyCurrency method
        private void CmdBuyCurrency(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("orangemart.buycurrency")) { player.Reply(Lang("NoPermission", player.Id)); return; }
            if (IsOnCooldown(player, "buy")) return;
            if (HasTooManyPendingInvoices(player)) return;

            if (args.Length != 1 || !int.TryParse(args[0], out int amount))
            {
                player.Reply(Lang("InvalidCommandUsage", player.Id, buyCurrencyCommandName));
                return;
            }

            if (!ValidatePurchaseAmount(player, amount, out int amountSats)) return;

            string transactionId = GenerateTransactionId();
            LogBuyInvoice(CreateBuyInvoiceLogEntry(player, null, false, amountSats, PurchaseType.Currency, 0));

            CreateInvoice(amountSats, $"Buying {amount} {currencyName}", invoiceResponse =>
            {
                if (invoiceResponse != null)
                {
                    UpdateBuyTransactionInvoiceId(transactionId, invoiceResponse.PaymentHash);
                    player.Reply(Lang("InvoiceCreatedCheckDiscord", player.Id, discordChannelName));

                    var pendingInvoice = new PendingInvoice
                    {
                        TransactionId = transactionId,
                        RHash = invoiceResponse.PaymentHash.ToLower(),
                        Player = player,
                        Amount = amount,
                        Memo = $"Buying {amount} {currencyName}",
                        CreatedAt = DateTime.UtcNow,
                        Type = PurchaseType.Currency
                    };
                    pendingInvoices.Add(pendingInvoice);
                    
                    SendInvoiceToDiscord(player, invoiceResponse.PaymentRequest, amountSats, $"Buying {amount} {currencyName}", pendingInvoice);

                    Task.Run(async () => await ConnectWebSocket(pendingInvoice));
                    ScheduleInvoiceExpiry(pendingInvoice);
                }
                else
                {
                    player.Reply(Lang("FailedToCreateInvoice", player.Id));
                    UpdateBuyTransactionStatus(transactionId, TransactionStatus.FAILED, false);
                }
            });
        }

        // Protected CmdSendCurrency method
        private void CmdSendCurrency(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("orangemart.sendcurrency")) { player.Reply(Lang("NoPermission", player.Id)); return; }
            if (IsOnCooldown(player, "send")) return;
            if (HasTooManyPendingInvoices(player)) return;

            if (args.Length != 2 || !int.TryParse(args[0], out int amount))
            {
                player.Reply(Lang("UsageSendCurrency", player.Id, sendCurrencyCommandName));
                return;
            }

            if (!ValidateSendAmount(player, amount, out int satsAmount)) return;

            string lightningAddress = args[1];
            if (!IsLightningAddressAllowed(lightningAddress))
            {
                player.Reply(Lang("BlacklistedDomain", player.Id, GetDomainFromLightningAddress(lightningAddress)));
                return;
            }

            var basePlayer = player.Object as BasePlayer;
            if (basePlayer == null) return;

            // Optimized Inventory Check
            if (!TryTakeCurrency(basePlayer, amount))
            {
                player.Reply(Lang("NeedMoreCurrency", player.Id, currencyName, amount));
                return;
            }

            string transactionId = GenerateTransactionId();
            LogSellTransaction(new SellInvoiceLogEntry
            {
                TransactionId = transactionId,
                SteamID = GetPlayerId(player),
                LightningAddress = lightningAddress,
                Status = TransactionStatus.INITIATED,
                Success = false,
                SatsAmount = satsAmount,
                Timestamp = DateTime.UtcNow
            });

            player.Reply(Lang("TransactionInitiated", player.Id));

            SendBitcoin(lightningAddress, satsAmount, (success, paymentHash) =>
            {
                if (success && !string.IsNullOrEmpty(paymentHash))
                {
                    UpdateSellTransactionPaymentHash(transactionId, paymentHash);

                    var pendingInvoice = new PendingInvoice
                    {
                        TransactionId = transactionId,
                        RHash = paymentHash.ToLower(),
                        Player = player,
                        Amount = satsAmount,
                        Memo = $"Sending {amount} {currencyName} to {lightningAddress}",
                        CreatedAt = DateTime.UtcNow,
                        Type = PurchaseType.SendBitcoin
                    };
                    
                    pendingInvoices.Add(pendingInvoice);
                    Puts($"Outbound payment to {lightningAddress} initiated. PaymentHash: {paymentHash}");

                    // CRITICAL FIX: Don't use WebSockets for sends. Check HTTP immediately.
                    // If it's already done, process it now. If not, the timer will catch it.
                    CheckInvoicePaid(paymentHash, isPaid => 
                    {
                        if (isPaid)
                        {
                            // Ensure ProcessPaymentConfirmation runs safely (it already checks list containment)
                            ProcessPaymentConfirmation(pendingInvoice);
                        }
                        // Else: Fall through to CheckPendingInvoices timer
                    });
                }
                else
                {
                    player.Reply(Lang("FailedToProcessPayment", player.Id));
                    UpdateSellTransactionStatus(transactionId, TransactionStatus.FAILED, false, "Failed to initiate payment", true);
                    
                    // Refund
                    ReturnCurrency(basePlayer, amount);
                }
            });
        }

        // Protected CmdBuyVip method
        private void CmdBuyVip(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("orangemart.buyvip")) { player.Reply(Lang("NoPermission", player.Id)); return; }
            if (IsOnCooldown(player, "vip")) return;
            if (HasTooManyPendingInvoices(player)) return;

            if (!ValidateVipPrice(player, out int amountSats)) return;

            string transactionId = GenerateTransactionId();
            LogBuyInvoice(CreateBuyInvoiceLogEntry(player, null, false, amountSats, PurchaseType.Vip, 0));

            CreateInvoice(amountSats, "Buying VIP Status", invoiceResponse =>
            {
                if (invoiceResponse != null)
                {
                    UpdateBuyTransactionInvoiceId(transactionId, invoiceResponse.PaymentHash);
                    player.Reply(Lang("InvoiceCreatedCheckDiscord", player.Id, discordChannelName));

                    var pendingInvoice = new PendingInvoice
                    {
                        TransactionId = transactionId,
                        RHash = invoiceResponse.PaymentHash.ToLower(),
                        Player = player,
                        Amount = amountSats,
                        Memo = "Buying VIP Status",
                        CreatedAt = DateTime.UtcNow,
                        Type = PurchaseType.Vip
                    };
                    pendingInvoices.Add(pendingInvoice);

                    SendInvoiceToDiscord(player, invoiceResponse.PaymentRequest, amountSats, "Buying VIP Status", pendingInvoice);
                    
                    Task.Run(async () => await ConnectWebSocket(pendingInvoice));
                    ScheduleInvoiceExpiry(pendingInvoice);
                }
                else
                {
                    player.Reply(Lang("FailedToCreateInvoice", player.Id));
                    UpdateBuyTransactionStatus(transactionId, TransactionStatus.FAILED, false);
                }
            });
        }

        [ChatCommand("orangelimits")]
        private void CmdPlayerLimits(BasePlayer player, string command, string[] args)
        {
            player.ChatMessage(Lang("ProtectionLimits", player.UserIDString, maxPurchaseAmount, maxSendAmount, commandCooldownSeconds));
        }

        // OPTIMIZED INVENTORY HELPER
        private bool TryTakeCurrency(BasePlayer player, int amount)
        {
            // Create a list to hold the collected items
            var collected = new List<Item>();
            
            // Take items from the player's inventory
            int taken = player.inventory.Take(collected, currencyItemID, amount);
            
            if (taken == amount)
            {
                // Success! We found enough items.
                // Since we are "using" them for payment, we destroy them.
                foreach (var item in collected)
                {
                    item.Remove();
                }
                return true;
            }

            // Failure! We didn't find enough.
            // Return the items we managed to take back to the player.
            foreach (var item in collected)
            {
                // Call the method directly (don't check if true/false)
                player.GiveItem(item);
                
                // Check if the item failed to find a parent (inventory full)
                if (item.parent == null)
                {
                    item.Drop(player.transform.position + new UnityEngine.Vector3(0f, 1.5f, 0f), UnityEngine.Vector3.zero);
                }
            }
            return false;
        }

        // Fallback HTTP polling for when WebSockets are disabled or fail
        private void CheckPendingInvoices()
        {
            var currentInvoices = pendingInvoices.ToList();

            foreach (var invoice in currentInvoices)
            {
                string localPaymentHash = invoice.RHash;
                
                CheckInvoicePaid(localPaymentHash, isPaid =>
                {
                    if (isPaid)
                    {
                        ProcessPaymentConfirmation(invoice);
                    }
                    else
                    {
                        if (!retryCounts.ContainsKey(localPaymentHash)) retryCounts[localPaymentHash] = 0;
                        retryCounts[localPaymentHash]++;

                        // IF MAX RETRIES HIT: Call the centralized expiry
                        if (retryCounts[localPaymentHash] >= maxRetries)
                        {
                            ExpireInvoice(invoice, "Max Retries Reached");
                        }
                    }
                });
            }
        }

        private void CheckInvoicePaid(string paymentHash, Action<bool> callback)
        {
            string url = $"{config.BaseUrl}/api/v1/payments/{paymentHash.ToLower()}";
            var headers = new Dictionary<string, string> { { "Content-Type", "application/json" }, { "X-Api-Key", config.ApiKey } };

            MakeWebRequest(url, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response)) { callback(false); return; }
                try
                {
                    var paymentStatus = JsonConvert.DeserializeObject<PaymentStatusResponse>(response);
                    callback(paymentStatus != null && paymentStatus.Paid);
                }
                catch { callback(false); }
            }, RequestMethod.GET, headers);
        }

        private bool IsLightningAddressAllowed(string lightningAddress)
        {
            string domain = GetDomainFromLightningAddress(lightningAddress);
            if (string.IsNullOrEmpty(domain)) return false;
            return whitelistedDomains.Any() ? whitelistedDomains.Contains(domain) : !blacklistedDomains.Contains(domain);
        }

        private string GetDomainFromLightningAddress(string lightningAddress)
        {
            if (string.IsNullOrEmpty(lightningAddress)) return null;
            var parts = lightningAddress.Split('@');
            return parts.Length == 2 ? parts[1].ToLower() : null;
        }

        private void SendBitcoin(string lightningAddress, int satsAmount, Action<bool, string> callback)
        {
            ResolveLightningAddress(lightningAddress, satsAmount, bolt11 =>
            {
                if (string.IsNullOrEmpty(bolt11)) { callback(false, null); return; }

                SendPayment(bolt11, satsAmount, (success, paymentHash) =>
                {
                    callback(success, paymentHash);
                });
            });
        }

        private void ScheduleInvoiceExpiry(PendingInvoice pendingInvoice)
        {
            timer.Once(invoiceTimeoutSeconds, () =>
            {
                // Only expire if it hasn't already been removed by the loop
                if (pendingInvoices.Contains(pendingInvoice))
                {
                    ExpireInvoice(pendingInvoice, "Timeout Timer");
                }
            });
        }

        private void SendPayment(string bolt11, int satsAmount, Action<bool, string> callback)
        {
            string url = $"{config.BaseUrl}/api/v1/payments";
            var jsonBody = JsonConvert.SerializeObject(new { @out = true, bolt11 = bolt11 });
            var headers = new Dictionary<string, string> { { "X-Api-Key", config.ApiKey }, { "Content-Type", "application/json" } };

            MakeWebRequest(url, jsonBody, (code, response) =>
            {
                if (code != 200 && code != 201) { callback(false, null); return; }
                try
                {
                    InvoiceResponse invoiceResponse = null;
                    try { invoiceResponse = JsonConvert.DeserializeObject<InvoiceResponseWrapper>(response)?.Data; } catch { }
                    if (invoiceResponse == null) invoiceResponse = JsonConvert.DeserializeObject<InvoiceResponse>(response);

                    if (!string.IsNullOrEmpty(invoiceResponse?.PaymentHash)) callback(true, invoiceResponse.PaymentHash);
                    else callback(false, null);
                }
                catch { callback(false, null); }
            }, RequestMethod.POST, headers);
        }

        private void CreateInvoice(int amountSats, string memo, Action<InvoiceResponse> callback)
        {
            string url = $"{config.BaseUrl}/api/v1/payments";
            var jsonBody = JsonConvert.SerializeObject(new { @out = false, amount = amountSats, memo = memo });
            var headers = new Dictionary<string, string> { { "X-Api-Key", config.ApiKey }, { "Content-Type", "application/json" } };

            MakeWebRequest(url, jsonBody, (code, response) =>
            {
                if (code != 200 && code != 201) { callback(null); return; }
                try
                {
                    var invoiceResponse = JsonConvert.DeserializeObject<InvoiceResponse>(response);
                    callback(invoiceResponse);
                }
                catch { callback(null); }
            }, RequestMethod.POST, headers);
        }

        private string GetPlayerId(IPlayer player)
        {
            return (player.Object as BasePlayer)?.UserIDString ?? player.Id;
        }

        private void MakeWebRequest(string url, string jsonData, Action<int, string> callback, RequestMethod method = RequestMethod.GET, Dictionary<string, string> headers = null)
        {
            webrequest.Enqueue(url, jsonData, (code, response) => callback(code, response), this, method, headers);
        }

        private void ResolveLightningAddress(string lightningAddress, int amountSats, Action<string> callback)
        {
            var parts = lightningAddress.Split('@');
            if (parts.Length != 2) { callback(null); return; }

            string lnurlEndpoint = $"https://{parts[1]}/.well-known/lnurlp/{parts[0]}";

            MakeWebRequest(lnurlEndpoint, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response)) { callback(null); return; }
                try
                {
                    var lnurlResponse = JsonConvert.DeserializeObject<LNURLResponse>(response);
                    if (lnurlResponse == null || string.IsNullOrEmpty(lnurlResponse.Callback)) { callback(null); return; }

                    long amountMsat = (long)amountSats * 1000;
                    string callbackUrl = $"{lnurlResponse.Callback}?amount={amountMsat}";

                    MakeWebRequest(callbackUrl, null, (payCode, payResponse) =>
                    {
                        if (payCode != 200 || string.IsNullOrEmpty(payResponse)) { callback(null); return; }
                        try
                        {
                            var payAction = JsonConvert.DeserializeObject<LNURLPayResponse>(payResponse);
                            callback(payAction?.Pr);
                        }
                        catch { callback(null); }
                    });
                }
                catch { callback(null); }
            });
        }

        private class LNURLResponse
        {
            [JsonProperty("callback")] public string Callback { get; set; }
        }

        private class LNURLPayResponse
        {
            [JsonProperty("pr")] public string Pr { get; set; }
        }

        private void RewardPlayer(IPlayer player, int amount)
        {
            var basePlayer = player.Object as BasePlayer;
            if (basePlayer == null) return;

            var currencyItem = ItemManager.CreateByItemID(currencyItemID, amount);
            if (currencyItem != null)
            {
                if (currencySkinID > 0) currencyItem.skin = currencySkinID;
                
                // Fixed logic: Call GiveItem, then check if parent is null
                basePlayer.GiveItem(currencyItem);

                if (currencyItem.parent == null)
                {
                    currencyItem.Drop(basePlayer.transform.position + new UnityEngine.Vector3(0f, 1.5f, 0f), UnityEngine.Vector3.zero);
                    player.Reply($"Inventory full! {amount} {currencyName} dropped on ground.");
                }
                else 
                {
                    player.Reply($"You have successfully purchased {amount} {currencyName}!");
                }
            }
        }

        private void GrantVip(IPlayer player)
        {
            player.Reply("You have successfully purchased VIP status!");
            string id = GetPlayerId(player);
            string cmd = vipCommand.Replace("{player}", player.Name).Replace("{steamid}", id).Replace("{userid}", id);
            server.Command(cmd);
        }

        private void ReturnCurrency(BasePlayer player, int amount)
        {
            var returnedCurrency = ItemManager.CreateByItemID(currencyItemID, amount);
            if (returnedCurrency != null)
            {
                if (currencySkinID > 0) returnedCurrency.skin = currencySkinID;
                
                // Fixed logic: Call GiveItem, then check if parent is null (meaning it failed to enter inventory)
                player.GiveItem(returnedCurrency);
                
                if (returnedCurrency.parent == null)
                {
                    returnedCurrency.Drop(player.transform.position + new UnityEngine.Vector3(0f, 1.5f, 0f), UnityEngine.Vector3.zero);
                }
                Puts($"Returned {amount} {currencyName} to player {player.UserIDString}.");
            }
        }

        // Logging Helpers
        private void LogSellTransaction(SellInvoiceLogEntry logEntry)
        {
            var logs = LoadSellLogData();
            var idx = logs.FindIndex(l => l.TransactionId == logEntry.TransactionId);
            if (idx >= 0) logs[idx] = logEntry; else logs.Add(logEntry);
            SaveSellLogData(logs);
        }

        private void UpdateSellTransactionStatus(string transactionId, string status, bool success, string failureReason = null, bool currencyReturned = false)
        {
            var logs = LoadSellLogData();
            var entry = logs.FirstOrDefault(l => l.TransactionId == transactionId);
            if (entry != null)
            {
                entry.Status = status;
                entry.Success = success;
                entry.CompletedTimestamp = DateTime.UtcNow;
                entry.CurrencyReturned = currencyReturned;
                if (!string.IsNullOrEmpty(failureReason)) entry.FailureReason = failureReason;
                SaveSellLogData(logs);
            }
        }

        private void UpdateSellTransactionPaymentHash(string transactionId, string paymentHash)
        {
            var logs = LoadSellLogData();
            var entry = logs.FirstOrDefault(l => l.TransactionId == transactionId);
            if (entry != null) { entry.PaymentHash = paymentHash; SaveSellLogData(logs); }
        }

        private List<SellInvoiceLogEntry> LoadSellLogData()
        {
            var path = Path.Combine(Interface.Oxide.DataDirectory, SellLogFile);
            return File.Exists(path) ? JsonConvert.DeserializeObject<List<SellInvoiceLogEntry>>(File.ReadAllText(path)) : new List<SellInvoiceLogEntry>();
        }

        private void SaveSellLogData(List<SellInvoiceLogEntry> data)
        {
            var path = Path.Combine(Interface.Oxide.DataDirectory, SellLogFile);
            if (!Directory.Exists(Path.GetDirectoryName(path))) Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        private void LogBuyInvoice(BuyInvoiceLogEntry logEntry)
        {
            var logs = LoadBuyLogData();
            var idx = logs.FindIndex(l => l.TransactionId == logEntry.TransactionId);
            if (idx >= 0) logs[idx] = logEntry; else logs.Add(logEntry);
            SaveBuyLogData(logs);
        }

        private void UpdateBuyTransactionStatus(string transactionId, string status, bool isPaid)
        {
            var logs = LoadBuyLogData();
            var entry = logs.FirstOrDefault(l => l.TransactionId == transactionId);
            if (entry != null)
            {
                entry.Status = status;
                entry.IsPaid = isPaid;
                entry.CompletedTimestamp = DateTime.UtcNow;
                if (isPaid)
                {
                    if (entry.PurchaseType == "Currency") entry.CurrencyGiven = true;
                    else if (entry.PurchaseType == "VIP") entry.VipGranted = true;
                }
                SaveBuyLogData(logs);
            }
        }

        private void UpdateBuyTransactionInvoiceId(string transactionId, string invoiceId)
        {
            var logs = LoadBuyLogData();
            var entry = logs.FirstOrDefault(l => l.TransactionId == transactionId);
            if (entry != null) { entry.InvoiceID = invoiceId; SaveBuyLogData(logs); }
        }

        private List<BuyInvoiceLogEntry> LoadBuyLogData()
        {
            var path = Path.Combine(Interface.Oxide.DataDirectory, BuyInvoiceLogFile);
            return File.Exists(path) ? JsonConvert.DeserializeObject<List<BuyInvoiceLogEntry>>(File.ReadAllText(path)) ?? new List<BuyInvoiceLogEntry>() : new List<BuyInvoiceLogEntry>();
        }

        private void SaveBuyLogData(List<BuyInvoiceLogEntry> data)
        {
            var path = Path.Combine(Interface.Oxide.DataDirectory, BuyInvoiceLogFile);
            if (!Directory.Exists(Path.GetDirectoryName(path))) Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        private BuyInvoiceLogEntry CreateBuyInvoiceLogEntry(IPlayer player, string invoiceID, bool isPaid, int amount, PurchaseType type, int retryCount)
        {
            return new BuyInvoiceLogEntry
            {
                TransactionId = GenerateTransactionId(),
                SteamID = GetPlayerId(player),
                InvoiceID = invoiceID,
                Status = isPaid ? TransactionStatus.COMPLETED : TransactionStatus.FAILED,
                IsPaid = isPaid,
                Timestamp = DateTime.UtcNow,
                CompletedTimestamp = DateTime.UtcNow,
                Amount = amount,
                CurrencyGiven = isPaid && type == PurchaseType.Currency,
                VipGranted = isPaid && type == PurchaseType.Vip,
                RetryCount = retryCount,
                PurchaseType = type == PurchaseType.Currency ? "Currency" : "VIP"
            };
        }

        private void SendInvoiceToDiscord(IPlayer player, string invoice, int amountSats, string memo, PendingInvoice pendingInvoice)
        {
            if (string.IsNullOrEmpty(config.DiscordWebhookUrl)) return;

            string qrCodeUrl = $"https://api.qrserver.com/v1/create-qr-code/?data={Uri.EscapeDataString(invoice)}&size=200x200";
            var payload = new
            {
                content = $"**{player.Name}**, please pay **{amountSats} sats**.",
                embeds = new[]
                {
                    new
                    {
                        title = "Payment Invoice",
                        description = $"{memo}\n\n```\n{invoice}\n```",
                        image = new { url = qrCodeUrl },
                        fields = new[] { new { name = "Amount", value = $"{amountSats} sats", inline = true } }
                    }
                }
            };

            var headers = new Dictionary<string, string> { { "Content-Type", "application/json" } };
            
            // ADDED: ?wait=true to the URL so Discord returns the message object
            string url = $"{config.DiscordWebhookUrl}?wait=true";

            MakeWebRequest(url, JsonConvert.SerializeObject(payload), (code, response) => 
            {
                if (code >= 200 && code < 300 && !string.IsNullOrEmpty(response))
                {
                    // Parse the ID and store it in the pending invoice
                    try 
                    {
                        var discordResponse = JsonConvert.DeserializeObject<Dictionary<string, object>>(response);
                        if (discordResponse.ContainsKey("id"))
                        {
                            pendingInvoice.DiscordMessageId = discordResponse["id"].ToString();
                        }
                    }
                    catch {}
                }
                else
                {
                    PrintError($"Failed to send Discord invoice. HTTP Code: {code}");
                }
            }, RequestMethod.POST, headers);
        }

        // NEW: Helper to delete discord messages
        // NEW: Helper to EDIT discord message to show expiry status
        private void EditDiscordMessage(string messageId, IPlayer player, int amountSats)
        {
            if (string.IsNullOrEmpty(config.DiscordWebhookUrl) || string.IsNullOrEmpty(messageId)) return;
            
            // Discord webhooks allow editing specific messages by appending /messages/{id}
            string editUrl = $"{config.DiscordWebhookUrl}/messages/{messageId}";
            
            var payload = new
            {
                content = $"~~**{player.Name}**, please pay **{amountSats} sats**.~~",
                embeds = new[]
                {
                    new
                    {
                        title = "Invoice Expired",
                        description = "This invoice has expired due to timeout. Please request a new one.",
                        color = 15158332, // Red color
                        fields = new[] { new { name = "Status", value = "EXPIRED", inline = true } }
                    }
                }
            };

            // PATCH is the HTTP method to Edit
            MakeWebRequest(editUrl, JsonConvert.SerializeObject(payload), (code, response) => 
            {
                if (code >= 200 && code < 300) Puts($"Marked Discord message {messageId} as expired.");
            }, RequestMethod.PATCH, new Dictionary<string, string> { { "Content-Type", "application/json" } });
        }

        // NEW: Helper to cancel LNbits payment (Stop attempting)
        private void CancelLNbitsPayment(string paymentHash)
        {
            string url = $"{config.BaseUrl}/api/v1/payments/{paymentHash}";
            var headers = new Dictionary<string, string> { { "X-Api-Key", config.ApiKey } };

            // Sending a DELETE request to LNbits removes the check
            MakeWebRequest(url, null, (code, response) =>
            {
                Puts($"Attempted to cancel/delete payment {paymentHash} in LNbits. Code: {code}");
            }, RequestMethod.DELETE, headers);
        }

        private void ExpireInvoice(PendingInvoice pendingInvoice, string reason)
        {
            // 1. Remove from list immediately
            if (pendingInvoices.Contains(pendingInvoice))
            {
                pendingInvoices.Remove(pendingInvoice);
            }

            // 2. Clean up WebSocket
            lock (webSocketLock)
            {
                if (activeWebSockets.ContainsKey(pendingInvoice.RHash))
                {
                    activeWebSockets[pendingInvoice.RHash].CancellationTokenSource?.Cancel();
                    activeWebSockets.Remove(pendingInvoice.RHash);
                }
            }
            
            // 3. Clean up Retry Counts
            if (retryCounts.ContainsKey(pendingInvoice.RHash))
            {
                retryCounts.Remove(pendingInvoice.RHash);
            }

            // 4. Update Discord Message (EDIT instead of Delete)
            if (!string.IsNullOrEmpty(pendingInvoice.DiscordMessageId))
            {
                EditDiscordMessage(pendingInvoice.DiscordMessageId, pendingInvoice.Player, pendingInvoice.Amount);
            }

            // 5. Cancel LNbits Payment (for both Buy and Sell to be safe, but mostly for Sell)
            // It doesn't hurt to try canceling an inbound invoice too.
            CancelLNbitsPayment(pendingInvoice.RHash);

            // 6. Handle specific types (Refunds or Status Updates)
            if (pendingInvoice.Type == PurchaseType.SendBitcoin)
            {
                var basePlayer = pendingInvoice.Player.Object as BasePlayer;
                if (basePlayer != null) 
                {
                    ReturnCurrency(basePlayer, pendingInvoice.Amount / satsPerCurrencyUnit);
                    pendingInvoice.Player.Reply("Payment timed out. Your currency has been refunded.");
                }
                UpdateSellTransactionStatus(pendingInvoice.TransactionId, TransactionStatus.EXPIRED, false, reason, true);
            }
            else
            {
                UpdateBuyTransactionStatus(pendingInvoice.TransactionId, TransactionStatus.EXPIRED, false);
                pendingInvoice.Player.Reply("Your purchase invoice has expired.");
            }
            
            Puts($"Invoice {pendingInvoice.RHash} expired. Reason: {reason}");
        }

    }
}