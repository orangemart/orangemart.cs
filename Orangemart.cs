using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Libraries;

namespace Oxide.Plugins
{
    [Info("Orangemart", "saulteafarmer", "0.3.0")]
    [Description("Allows players to buy and sell in-game units and VIP status using Bitcoin Lightning Network payments via LNbits")]
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

            // VIPSettings
            public const string VipPermissionGroup = "VipPermissionGroup";
            public const string VipPrice = "VipPrice";
        }

        // Configuration variables
        private int currencyItemID;
        private string buyCurrencyCommandName;
        private string sendCurrencyCommandName;
        private string buyVipCommandName;
        private int vipPrice;
        private string vipPermissionGroup;
        private string currencyName;
        private int satsPerCurrencyUnit;
        private int pricePerCurrencyUnit;
        private string discordChannelName;
        private ulong currencySkinID;
        private int checkIntervalSeconds;
        private int invoiceTimeoutSeconds;
        private int maxRetries;
        private List<string> blacklistedDomains = new List<string>();
        private List<string> whitelistedDomains = new List<string>();
        private const string SellLogFile = "Orangemart/send_bitcoin.json";
        private const string BuyInvoiceLogFile = "Orangemart/buy_invoices.json";
        private LNbitsConfig config;
        private List<PendingInvoice> pendingInvoices = new List<PendingInvoice>();
        private Dictionary<string, int> retryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // LNbits Configuration
        private class LNbitsConfig
        {
            public string BaseUrl { get; set; }
            public string ApiKey { get; set; }
            public string DiscordWebhookUrl { get; set; }

            public static LNbitsConfig ParseLNbitsConnection(string baseUrl, string apiKey, string discordWebhookUrl)
            {
                var trimmedBaseUrl = baseUrl.TrimEnd('/');
                if (!Uri.IsWellFormedUriString(trimmedBaseUrl, UriKind.Absolute))
                    throw new Exception("Invalid base URL in connection string.");

                return new LNbitsConfig
                {
                    BaseUrl = trimmedBaseUrl,
                    ApiKey = apiKey,
                    DiscordWebhookUrl = discordWebhookUrl
                };
            }
        }

        // Invoice and Payment Classes
        private class InvoiceResponse
        {
            [JsonProperty("payment_request")]
            public string PaymentRequest { get; set; }

            [JsonProperty("payment_hash")]
            public string PaymentHash { get; set; }
        }

        private class SellInvoiceLogEntry
        {
            public string SteamID { get; set; }
            public string LightningAddress { get; set; }
            public bool Success { get; set; }
            public int SatsAmount { get; set; }
            public string PaymentHash { get; set; }
            public bool CurrencyReturned { get; set; }
            public DateTime Timestamp { get; set; }
            public int RetryCount { get; set; }
        }

        private class BuyInvoiceLogEntry
        {
            public string SteamID { get; set; }
            public string InvoiceID { get; set; }
            public bool IsPaid { get; set; }
            public DateTime Timestamp { get; set; }
            public int Amount { get; set; }
            public bool CurrencyGiven { get; set; }
            public bool VipGranted { get; set; }
            public int RetryCount { get; set; }
        }

        private class PendingInvoice
        {
            public string RHash { get; set; }
            public IPlayer Player { get; set; }
            public int Amount { get; set; }
            public string Memo { get; set; }
            public DateTime CreatedAt { get; set; }
            public PurchaseType Type { get; set; }
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

                // Parse Command Names
                buyCurrencyCommandName = GetConfigValue(ConfigSections.Commands, ConfigKeys.BuyCurrencyCommandName, "buyblood", ref configChanged);
                sendCurrencyCommandName = GetConfigValue(ConfigSections.Commands, ConfigKeys.SendCurrencyCommandName, "sendblood", ref configChanged);
                buyVipCommandName = GetConfigValue(ConfigSections.Commands, ConfigKeys.BuyVipCommandName, "buyvip", ref configChanged);

                // Parse VIP Settings
                vipPrice = GetConfigValue(ConfigSections.VIPSettings, ConfigKeys.VipPrice, 1000, ref configChanged);
                vipPermissionGroup = GetConfigValue(ConfigSections.VIPSettings, ConfigKeys.VipPermissionGroup, "vip", ref configChanged);

                // Parse Discord Settings
                discordChannelName = GetConfigValue(ConfigSections.Discord, ConfigKeys.DiscordChannelName, "mart", ref configChanged);

                // Parse Invoice Settings
                checkIntervalSeconds = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.CheckIntervalSeconds, 10, ref configChanged);
                invoiceTimeoutSeconds = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.InvoiceTimeoutSeconds, 300, ref configChanged);
                maxRetries = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.MaxRetries, 25, ref configChanged);

                blacklistedDomains = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.BlacklistedDomains, new List<string> { "example.com", "blacklisted.net" }, ref configChanged)
                    .Select(d => d.ToLower()).ToList();

                whitelistedDomains = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.WhitelistedDomains, new List<string>(), ref configChanged)
                    .Select(d => d.ToLower()).ToList();

                MigrateConfig();

                if (configChanged)
                {
                    SaveConfig();
                }
            }
            catch (Exception ex)
            {
                PrintError($"Failed to load configuration: {ex.Message}");
            }
        }

        private void MigrateConfig()
        {
            bool configChanged = false;

            if (!(Config[ConfigSections.InvoiceSettings] is Dictionary<string, object> invoiceSettings))
            {
                invoiceSettings = new Dictionary<string, object>();
                Config[ConfigSections.InvoiceSettings] = invoiceSettings;
                configChanged = true;
            }

            if (!invoiceSettings.ContainsKey(ConfigKeys.WhitelistedDomains))
            {
                invoiceSettings[ConfigKeys.WhitelistedDomains] = new List<string>();
                configChanged = true;
            }

            if (configChanged)
            {
                SaveConfig();
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
                if (value is T tValue)
                {
                    return tValue;
                }
                else if (typeof(T) == typeof(List<string>))
                {
                    if (value is IEnumerable<object> enumerable)
                    {
                        return (T)(object)enumerable.Select(item => item.ToString()).ToList();
                    }
                    else if (value is string singleString)
                    {
                        return (T)(object)new List<string> { singleString };
                    }
                    else
                    {
                        PrintError($"Unexpected type for [{section}][{key}]. Using default value.");
                        data[key] = defaultValue;
                        configChanged = true;
                        return defaultValue;
                    }
                }
                else if (typeof(T) == typeof(ulong))
                {
                    if (value is long longVal)
                    {
                        return (T)(object)(ulong)longVal;
                    }
                    else if (value is ulong ulongVal)
                    {
                        return (T)(object)ulongVal;
                    }
                    else
                    {
                        return (T)Convert.ChangeType(value, typeof(T));
                    }
                }
                else
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
            }
            catch (Exception ex)
            {
                PrintError($"Error converting config value for [{section}][{key}]: {ex.Message}. Using default value.");
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
                [ConfigKeys.SatsPerCurrencyUnit] = 1
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
                [ConfigKeys.MaxRetries] = 25
            };

            Config[ConfigSections.VIPSettings] = new Dictionary<string, object>
            {
                [ConfigKeys.VipPermissionGroup] = "vip",
                [ConfigKeys.VipPrice] = 1000
            };
        }

        private void Init()
        {
            // Register permissions
            permission.RegisterPermission("orangemart.buycurrency", this);
            permission.RegisterPermission("orangemart.sendcurrency", this);
            permission.RegisterPermission("orangemart.buyvip", this);
        }

        private void OnServerInitialized()
        {
            if (config == null)
            {
                PrintError("Plugin configuration is not properly set up. Please check your configuration file.");
                return;
            }

            // Register commands
            AddCovalenceCommand(buyCurrencyCommandName, nameof(CmdBuyCurrency), "orangemart.buycurrency");
            AddCovalenceCommand(sendCurrencyCommandName, nameof(CmdSendCurrency), "orangemart.sendcurrency");
            AddCovalenceCommand(buyVipCommandName, nameof(CmdBuyVip), "orangemart.buyvip");

            // Start a timer to check pending invoices periodically
            timer.Every(checkIntervalSeconds, CheckPendingInvoices);
        }

        private void Unload()
        {
            pendingInvoices.Clear();
            retryCounts.Clear();
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
                ["InvoiceCreatedCheckDiscord"] = "Invoice created! Please check the #{0} channel on Discord to complete your payment.",
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
                ["BlacklistedDomain"] = "The domain '{0}' is currently blacklisted. Please use a different Lightning address.",
                ["NotWhitelistedDomain"] = "The domain '{0}' is not whitelisted. Please use a Lightning address from the following domains: {1}.",
                ["InvalidLightningAddress"] = "The Lightning Address provided is invalid or cannot be resolved.",
                ["PaymentProcessing"] = "Your payment is being processed. You will receive a confirmation once it's complete."
            }, this);
        }

        private string Lang(string key, string userId = null, params object[] args)
        {
            return string.Format(lang.GetMessage(key, this, userId), args);
        }

        private List<Item> GetAllInventoryItems(BasePlayer player)
        {
            List<Item> allItems = new List<Item>();

            // Main Inventory
            if (player.inventory.containerMain != null)
                allItems.AddRange(player.inventory.containerMain.itemList);

            // Belt (Hotbar)
            if (player.inventory.containerBelt != null)
                allItems.AddRange(player.inventory.containerBelt.itemList);

            // Wear (Clothing)
            if (player.inventory.containerWear != null)
                allItems.AddRange(player.inventory.containerWear.itemList);

            return allItems;
        }

        private void CheckPendingInvoices()
        {
            foreach (var invoice in pendingInvoices.ToList())
            {
                string localPaymentHash = invoice.RHash.ToLower();
                CheckInvoicePaid(localPaymentHash, isPaid =>
                {
                    if (isPaid)
                    {
                        pendingInvoices.Remove(invoice);

                        switch (invoice.Type)
                        {
                            case PurchaseType.Currency:
                                RewardPlayer(invoice.Player, invoice.Amount);
                                break;
                            case PurchaseType.Vip:
                                GrantVip(invoice.Player);
                                break;
                            case PurchaseType.SendBitcoin:
                                invoice.Player.Reply(Lang("CurrencySentSuccess", invoice.Player.Id, invoice.Amount, currencyName));
                                break;
                        }

                        if (invoice.Type == PurchaseType.SendBitcoin)
                        {
                            var logEntry = new SellInvoiceLogEntry
                            {
                                SteamID = GetPlayerId(invoice.Player),
                                LightningAddress = ExtractLightningAddress(invoice.Memo),
                                Success = true,
                                SatsAmount = invoice.Amount,
                                PaymentHash = invoice.RHash,
                                CurrencyReturned = false,
                                Timestamp = DateTime.UtcNow,
                                RetryCount = retryCounts.ContainsKey(invoice.RHash) ? retryCounts[invoice.RHash] : 0
                            };
                            LogSellTransaction(logEntry);

                            Puts($"Invoice {invoice.RHash} marked as paid. RetryCount: {logEntry.RetryCount}");
                        }
                        else
                        {
                            var logEntry = CreateBuyInvoiceLogEntry(
                                player: invoice.Player,
                                invoiceID: invoice.RHash,
                                isPaid: true,
                                amount: invoice.Type == PurchaseType.SendBitcoin ? invoice.Amount : invoice.Amount * pricePerCurrencyUnit,
                                type: invoice.Type,
                                retryCount: retryCounts.ContainsKey(invoice.RHash) ? retryCounts[invoice.RHash] : 0
                            );
                            LogBuyInvoice(logEntry);
                            Puts($"Invoice {invoice.RHash} marked as paid. RetryCount: {logEntry.RetryCount}");
                        }

                        retryCounts.Remove(invoice.RHash);
                    }
                    else
                    {
                        if (!retryCounts.ContainsKey(localPaymentHash))
                        {
                            retryCounts[localPaymentHash] = 0;
                            Puts($"Initialized retry count for paymentHash: {localPaymentHash}");
                        }

                        retryCounts[localPaymentHash]++;
                        Puts($"Retry count for paymentHash {localPaymentHash}: {retryCounts[localPaymentHash]} of {maxRetries}");

                        if (retryCounts[localPaymentHash] >= maxRetries)
                        {
                            pendingInvoices.Remove(invoice);
                            int finalRetryCount = retryCounts[localPaymentHash];
                            retryCounts.Remove(localPaymentHash);
                            PrintWarning($"Invoice for player {GetPlayerId(invoice.Player)} expired (amount: {invoice.Amount} sats).");

                            invoice.Player.Reply(Lang("InvoiceExpired", invoice.Player.Id, invoice.Amount));

                            if (invoice.Type == PurchaseType.SendBitcoin)
                            {
                                var basePlayer = invoice.Player.Object as BasePlayer;
                                if (basePlayer != null)
                                {
                                    ReturnCurrency(basePlayer, invoice.Amount / satsPerCurrencyUnit);
                                    Puts($"Refunded {invoice.Amount / satsPerCurrencyUnit} {currencyName} to player {basePlayer.UserIDString} due to failed payment.");
                                }
                                else
                                {
                                    PrintError($"Failed to find base player object for player {invoice.Player.Id} to refund currency.");
                                }

                                var failedLogEntry = new SellInvoiceLogEntry
                                {
                                    SteamID = GetPlayerId(invoice.Player),
                                    LightningAddress = ExtractLightningAddress(invoice.Memo),
                                    Success = false,
                                    SatsAmount = invoice.Amount,
                                    PaymentHash = invoice.RHash,
                                    CurrencyReturned = true,
                                    Timestamp = DateTime.UtcNow,
                                    RetryCount = finalRetryCount
                                };
                                LogSellTransaction(failedLogEntry);
                                Puts($"Invoice {localPaymentHash} expired after {finalRetryCount} retries.");
                            }
                            else
                            {
                                var failedLogEntry = CreateBuyInvoiceLogEntry(
                                    player: invoice.Player,
                                    invoiceID: localPaymentHash,
                                    isPaid: false,
                                    amount: invoice.Type == PurchaseType.SendBitcoin ? invoice.Amount : invoice.Amount * pricePerCurrencyUnit,
                                    type: invoice.Type,
                                    retryCount: finalRetryCount
                                );
                                LogBuyInvoice(failedLogEntry);
                                Puts($"Invoice {localPaymentHash} expired after {finalRetryCount} retries.");
                            }
                        }
                        else
                        {
                            PrintWarning($"Retrying invoice {localPaymentHash}. Attempt {retryCounts[localPaymentHash]} of {maxRetries}.");
                        }
                    }
                });
            }
        }

        private void CheckInvoicePaid(string paymentHash, Action<bool> callback)
        {
            string normalizedPaymentHash = paymentHash.ToLower();
            string url = $"{config.BaseUrl}/api/v1/payments/{normalizedPaymentHash}";

            var headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" },
                { "X-Api-Key", config.ApiKey }
            };

            MakeWebRequest(url, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    PrintError($"Error checking invoice status: HTTP {code}");
                    callback(false);
                    return;
                }

                try
                {
                    var paymentStatus = JsonConvert.DeserializeObject<PaymentStatusResponse>(response);
                    callback(paymentStatus != null && paymentStatus.Paid);
                }
                catch (Exception ex)
                {
                    PrintError($"Failed to parse invoice status response: {ex.Message}");
                    callback(false);
                }
            }, RequestMethod.GET, headers);
        }

        private void CmdSendCurrency(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("orangemart.sendcurrency"))
            {
                player.Reply(Lang("NoPermission", player.Id));
                return;
            }

            if (args.Length != 2 || !int.TryParse(args[0], out int amount) || amount <= 0)
            {
                player.Reply(Lang("UsageSendCurrency", player.Id, sendCurrencyCommandName));
                return;
            }

            string lightningAddress = args[1];

            if (!IsLightningAddressAllowed(lightningAddress))
            {
                string domain = GetDomainFromLightningAddress(lightningAddress);
                if (whitelistedDomains.Any())
                {
                    string whitelist = string.Join(", ", whitelistedDomains);
                    player.Reply(Lang("NotWhitelistedDomain", player.Id, domain, whitelist));
                }
                else
                {
                    player.Reply(Lang("BlacklistedDomain", player.Id, domain));
                }
                return;
            }

            var basePlayer = player.Object as BasePlayer;
            if (basePlayer == null)
            {
                player.Reply(Lang("FailedToFindBasePlayer", player.Id));
                return;
            }

            int currencyAmount = GetAllInventoryItems(basePlayer).Where(IsCurrencyItem).Sum(item => item.amount);

            if (currencyAmount < amount)
            {
                player.Reply(Lang("NeedMoreCurrency", player.Id, currencyName, currencyAmount));
                return;
            }

            if (!TryReserveCurrency(basePlayer, amount))
            {
                player.Reply(Lang("FailedToReserveCurrency", player.Id));
                return;
            }

            player.Reply(Lang("PaymentProcessing", player.Id));

            SendBitcoin(lightningAddress, amount * satsPerCurrencyUnit, (success, paymentHash) =>
            {
                if (success && !string.IsNullOrEmpty(paymentHash))
                {
                    LogSellTransaction(
                        new SellInvoiceLogEntry
                        {
                            SteamID = GetPlayerId(player),
                            LightningAddress = lightningAddress,
                            Success = true,
                            SatsAmount = amount * satsPerCurrencyUnit,
                            PaymentHash = paymentHash,
                            CurrencyReturned = false,
                            Timestamp = DateTime.UtcNow,
                            RetryCount = 0
                        }
                    );

                    var pendingInvoice = new PendingInvoice
                    {
                        RHash = paymentHash.ToLower(),
                        Player = player,
                        Amount = amount * satsPerCurrencyUnit,
                        Memo = $"Sending {amount} {currencyName} to {lightningAddress}",
                        CreatedAt = DateTime.UtcNow,
                        Type = PurchaseType.SendBitcoin
                    };
                    pendingInvoices.Add(pendingInvoice);

                    Puts($"Outbound payment to {lightningAddress} initiated. PaymentHash: {paymentHash}");
                }
                else
                {
                    player.Reply(Lang("FailedToProcessPayment", player.Id));

                    LogSellTransaction(
                        new SellInvoiceLogEntry
                        {
                            SteamID = GetPlayerId(player),
                            LightningAddress = lightningAddress,
                            Success = false,
                            SatsAmount = amount * satsPerCurrencyUnit,
                            PaymentHash = null,
                            CurrencyReturned = true,
                            Timestamp = DateTime.UtcNow,
                            RetryCount = 0
                        }
                    );

                    Puts($"Outbound payment to {lightningAddress} failed to initiate.");

                    ReturnCurrency(basePlayer, amount);
                    Puts($"Returned {amount} {currencyName} to player {basePlayer.UserIDString} due to failed payment.");
                }
            });
        }

        private bool IsLightningAddressAllowed(string lightningAddress)
        {
            string domain = GetDomainFromLightningAddress(lightningAddress);
            if (string.IsNullOrEmpty(domain))
                return false;

            if (whitelistedDomains.Any())
            {
                return whitelistedDomains.Contains(domain.ToLower());
            }
            else
            {
                return !blacklistedDomains.Contains(domain.ToLower());
            }
        }

        private string GetDomainFromLightningAddress(string lightningAddress)
        {
            if (string.IsNullOrEmpty(lightningAddress))
                return null;

            var parts = lightningAddress.Split('@');
            return parts.Length == 2 ? parts[1].ToLower() : null;
        }

        private void SendBitcoin(string lightningAddress, int satsAmount, Action<bool, string> callback)
        {
            ResolveLightningAddress(lightningAddress, satsAmount, bolt11 =>
            {
                if (string.IsNullOrEmpty(bolt11))
                {
                    PrintError($"Failed to resolve Lightning Address: {lightningAddress}");
                    callback(false, null);
                    return;
                }

                SendPayment(bolt11, satsAmount, (success, paymentHash) =>
                {
                    if (success && !string.IsNullOrEmpty(paymentHash))
                    {
                        StartPaymentStatusCheck(paymentHash, isPaid =>
                        {
                            callback(isPaid, isPaid ? paymentHash : null);
                        });
                    }
                    else
                    {
                        callback(false, null);
                    }
                });
            });
        }

        private void StartPaymentStatusCheck(string paymentHash, Action<bool> callback)
        {
            if (!retryCounts.ContainsKey(paymentHash))
            {
                retryCounts[paymentHash] = 0;
            }

            Timer timerInstance = null;
            timerInstance = timer.Repeat(checkIntervalSeconds, maxRetries, () =>
            {
                CheckInvoicePaid(paymentHash, isPaid =>
                {
                    if (isPaid)
                    {
                        callback(true);
                        timerInstance.Destroy();
                    }
                    else
                    {
                        retryCounts[paymentHash]++;
                        Puts($"PaymentHash {paymentHash} not yet paid. Retry {retryCounts[paymentHash]} of {maxRetries}.");

                        if (retryCounts[paymentHash] >= maxRetries)
                        {
                            callback(false);
                            timerInstance.Destroy();
                        }
                    }
                });
            });
        }

        private void CmdBuyCurrency(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("orangemart.buycurrency"))
            {
                player.Reply(Lang("NoPermission", player.Id));
                return;
            }

            if (args.Length != 1 || !int.TryParse(args[0], out int amount) || amount <= 0)
            {
                player.Reply(Lang("InvalidCommandUsage", player.Id, buyCurrencyCommandName));
                return;
            }

            int amountSats = amount * pricePerCurrencyUnit;

            CreateInvoice(amountSats, $"Buying {amount} {currencyName}", invoiceResponse =>
            {
                if (invoiceResponse != null)
                {
                    SendInvoiceToDiscord(player, invoiceResponse.PaymentRequest, amountSats, $"Buying {amount} {currencyName}");

                    player.Reply(Lang("InvoiceCreatedCheckDiscord", player.Id, discordChannelName));

                    var pendingInvoice = new PendingInvoice
                    {
                        RHash = invoiceResponse.PaymentHash.ToLower(),
                        Player = player,
                        Amount = amount,
                        Memo = $"Buying {amount} {currencyName}",
                        CreatedAt = DateTime.UtcNow,
                        Type = PurchaseType.Currency
                    };
                    pendingInvoices.Add(pendingInvoice);

                    ScheduleInvoiceExpiry(pendingInvoice);
                }
                else
                {
                    player.Reply(Lang("FailedToCreateInvoice", player.Id));
                }
            });
        }

        private void CmdBuyVip(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("orangemart.buyvip"))
            {
                player.Reply(Lang("NoPermission", player.Id));
                return;
            }

            int amountSats = vipPrice;

            CreateInvoice(amountSats, "Buying VIP Status", invoiceResponse =>
            {
                if (invoiceResponse != null)
                {
                    SendInvoiceToDiscord(player, invoiceResponse.PaymentRequest, amountSats, "Buying VIP Status");

                    player.Reply(Lang("InvoiceCreatedCheckDiscord", player.Id, discordChannelName));

                    var pendingInvoice = new PendingInvoice
                    {
                        RHash = invoiceResponse.PaymentHash.ToLower(),
                        Player = player,
                        Amount = amountSats,
                        Memo = "Buying VIP Status",
                        CreatedAt = DateTime.UtcNow,
                        Type = PurchaseType.Vip
                    };
                    pendingInvoices.Add(pendingInvoice);

                    ScheduleInvoiceExpiry(pendingInvoice);
                }
                else
                {
                    player.Reply(Lang("FailedToCreateInvoice", player.Id));
                }
            });
        }

        private void ScheduleInvoiceExpiry(PendingInvoice pendingInvoice)
        {
            timer.Once(invoiceTimeoutSeconds, () =>
            {
                if (pendingInvoices.Contains(pendingInvoice))
                {
                    pendingInvoices.Remove(pendingInvoice);
                    PrintWarning($"Invoice for player {GetPlayerId(pendingInvoice.Player)} expired (amount: {pendingInvoice.Amount} sats).");

                    int finalRetryCount = retryCounts.ContainsKey(pendingInvoice.RHash) ? retryCounts[pendingInvoice.RHash] : 0;

                    if (pendingInvoice.Type == PurchaseType.SendBitcoin)
                    {
                        var basePlayer = pendingInvoice.Player.Object as BasePlayer;
                        if (basePlayer != null)
                        {
                            ReturnCurrency(basePlayer, pendingInvoice.Amount / satsPerCurrencyUnit);
                            Puts($"Refunded {pendingInvoice.Amount / satsPerCurrencyUnit} {currencyName} to player {basePlayer.UserIDString} due to failed payment.");
                        }
                        else
                        {
                            PrintError($"Failed to find base player object for player {pendingInvoice.Player.Id} to refund currency.");
                        }

                        var logEntry = new SellInvoiceLogEntry
                        {
                            SteamID = GetPlayerId(pendingInvoice.Player),
                            LightningAddress = ExtractLightningAddress(pendingInvoice.Memo),
                            Success = false,
                            SatsAmount = pendingInvoice.Amount,
                            PaymentHash = pendingInvoice.RHash,
                            CurrencyReturned = true,
                            Timestamp = DateTime.UtcNow,
                            RetryCount = finalRetryCount
                        };
                        LogSellTransaction(logEntry);
                        Puts($"Invoice {pendingInvoice.RHash} for player {GetPlayerId(pendingInvoice.Player)} expired and logged.");
                    }
                    else
                    {
                        var logEntry = CreateBuyInvoiceLogEntry(
                            player: pendingInvoice.Player,
                            invoiceID: pendingInvoice.RHash,
                            isPaid: false,
                            amount: pendingInvoice.Type == PurchaseType.SendBitcoin ? pendingInvoice.Amount : pendingInvoice.Amount * pricePerCurrencyUnit,
                            type: pendingInvoice.Type,
                            retryCount: finalRetryCount
                        );
                        LogBuyInvoice(logEntry);
                        Puts($"Invoice {pendingInvoice.RHash} for player {GetPlayerId(pendingInvoice.Player)} expired and logged.");
                    }
                }
            });
        }

        private void SendPayment(string bolt11, int satsAmount, Action<bool, string> callback)
        {
            string url = $"{config.BaseUrl}/api/v1/payments";
            var requestBody = new
            {
                @out = true,
                bolt11 = bolt11,
                amount = satsAmount
            };
            string jsonBody = JsonConvert.SerializeObject(requestBody);

            var headers = new Dictionary<string, string>
            {
                { "X-Api-Key", config.ApiKey },
                { "Content-Type", "application/json" }
            };

            MakeWebRequest(url, jsonBody, (code, response) =>
            {
                if (code != 200 && code != 201)
                {
                    PrintError($"Error processing payment: HTTP {code}");
                    callback(false, null);
                    return;
                }

                try
                {
                    var paymentResponse = JsonConvert.DeserializeObject<Dictionary<string, object>>(response);
                    string paymentHash = paymentResponse.ContainsKey("payment_hash") ? paymentResponse["payment_hash"].ToString() : null;

                    if (!string.IsNullOrEmpty(paymentHash))
                    {
                        callback(true, paymentHash);
                    }
                    else
                    {
                        PrintError("Payment hash (rhash) is missing or invalid in the response.");
                        callback(false, null);
                    }
                }
                catch (Exception ex)
                {
                    PrintError($"Exception occurred while parsing payment response: {ex.Message}");
                    callback(false, null);
                }
            }, RequestMethod.POST, headers);
        }

        private void SendInvoiceToDiscord(IPlayer player, string invoice, int amountSats, string memo)
        {
            if (string.IsNullOrEmpty(config.DiscordWebhookUrl))
            {
                PrintError("Discord webhook URL is not configured.");
                return;
            }

            string qrCodeUrl = $"https://api.qrserver.com/v1/create-qr-code/?data={Uri.EscapeDataString(invoice)}&size=200x200";

            var webhookPayload = new
            {
                content = $"**{player.Name}**, please pay **{amountSats} sats** using the Lightning Network.",
                embeds = new[]
                {
                    new
                    {
                        title = "Payment Invoice",
                        description = $"{memo}\n\nPlease pay the following Lightning invoice to complete your purchase:\n\n```\n{invoice}\n```",
                        image = new
                        {
                            url = qrCodeUrl
                        },
                        fields = new[]
                        {
                            new { name = "Amount", value = $"{amountSats} sats", inline = true },
                            new { name = "Steam ID", value = GetPlayerId(player), inline = true }
                        }
                    }
                }
            };

            string jsonPayload = JsonConvert.SerializeObject(webhookPayload);

            MakeWebRequest(config.DiscordWebhookUrl, jsonPayload, (code, response) =>
            {
                if (code != 204)
                {
                    PrintError($"Failed to send invoice to Discord webhook: HTTP {code}");
                }
                else
                {
                    Puts($"Invoice sent to Discord for player {GetPlayerId(player)}.");
                }
            }, RequestMethod.POST, new Dictionary<string, string> { { "Content-Type", "application/json" } });
        }

        private void RewardPlayer(IPlayer player, int amount)
        {
            player.Reply($"You have successfully purchased {amount} {currencyName}!");

            var basePlayer = player.Object as BasePlayer;

            if (basePlayer != null)
            {
                var currencyItem = ItemManager.CreateByItemID(currencyItemID, amount);
                if (currencyItem != null)
                {
                    if (currencySkinID > 0)
                    {
                        currencyItem.skin = currencySkinID;
                    }

                    basePlayer.GiveItem(currencyItem);
                    Puts($"Gave {amount} {currencyName} (skinID: {currencySkinID}) to player {basePlayer.UserIDString}.");
                }
                else
                {
                    PrintError($"Failed to create {currencyName} item for player {basePlayer.UserIDString}.");
                }
            }
            else
            {
                PrintError($"Failed to find base player object for player {player.Id}.");
            }
        }

        private void GrantVip(IPlayer player)
        {
            player.Reply("You have successfully purchased VIP status!");

            permission.AddUserGroup(player.Id, vipPermissionGroup);

            Puts($"Player {GetPlayerId(player)} added to VIP group '{vipPermissionGroup}'.");
        }

        private bool TryReserveCurrency(BasePlayer player, int amount)
        {
            var items = GetAllInventoryItems(player).Where(IsCurrencyItem).ToList();
            int totalCurrency = items.Sum(item => item.amount);

            if (totalCurrency < amount)
            {
                return false;
            }

            int remaining = amount;

            foreach (var item in items)
            {
                if (item.amount > remaining)
                {
                    item.UseItem(remaining);
                    break;
                }
                else
                {
                    remaining -= item.amount;
                    item.Remove();
                }

                if (remaining <= 0)
                {
                    break;
                }
            }

            return true;
        }

        private void ReturnCurrency(BasePlayer player, int amount)
        {
            var returnedCurrency = ItemManager.CreateByItemID(currencyItemID, amount);
            if (returnedCurrency != null)
            {
                if (currencySkinID > 0)
                {
                    returnedCurrency.skin = currencySkinID;
                }
                returnedCurrency.MoveToContainer(player.inventory.containerMain);
            }
            else
            {
                PrintError($"Failed to create {currencyName} item to return to player {player.UserIDString}.");
            }
        }

        private bool IsCurrencyItem(Item item)
        {
            return item.info.itemid == currencyItemID && (currencySkinID == 0 || item.skin == currencySkinID);
        }

        private void LogSellTransaction(SellInvoiceLogEntry logEntry)
        {
            var logs = LoadSellLogData();
            logs.Add(logEntry);
            SaveSellLogData(logs);
            Puts($"[Orangemart] Logged sell transaction: {JsonConvert.SerializeObject(logEntry)}");
        }

        private List<SellInvoiceLogEntry> LoadSellLogData()
        {
            var path = Path.Combine(Interface.Oxide.DataDirectory, SellLogFile);
            return File.Exists(path)
                ? JsonConvert.DeserializeObject<List<SellInvoiceLogEntry>>(File.ReadAllText(path))
                : new List<SellInvoiceLogEntry>();
        }

        private void SaveSellLogData(List<SellInvoiceLogEntry> data)
        {
            var path = Path.Combine(Interface.Oxide.DataDirectory, SellLogFile);
            var directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        private void LogBuyInvoice(BuyInvoiceLogEntry logEntry)
        {
            var logPath = Path.Combine(Interface.Oxide.DataDirectory, BuyInvoiceLogFile);
            var directory = Path.GetDirectoryName(logPath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            List<BuyInvoiceLogEntry> invoiceLogs = File.Exists(logPath)
                ? JsonConvert.DeserializeObject<List<BuyInvoiceLogEntry>>(File.ReadAllText(logPath)) ?? new List<BuyInvoiceLogEntry>()
                : new List<BuyInvoiceLogEntry>();

            invoiceLogs.Add(logEntry);
            File.WriteAllText(logPath, JsonConvert.SerializeObject(invoiceLogs, Formatting.Indented));
            Puts($"[Orangemart] Logged buy invoice: {JsonConvert.SerializeObject(logEntry)}");
        }

        private BuyInvoiceLogEntry CreateBuyInvoiceLogEntry(IPlayer player, string invoiceID, bool isPaid, int amount, PurchaseType type, int retryCount)
        {
            return new BuyInvoiceLogEntry
            {
                SteamID = GetPlayerId(player),
                InvoiceID = invoiceID,
                IsPaid = isPaid,
                Timestamp = DateTime.UtcNow,
                Amount = type == PurchaseType.SendBitcoin ? amount : amount * pricePerCurrencyUnit,
                CurrencyGiven = isPaid && type == PurchaseType.Currency,
                VipGranted = isPaid && type == PurchaseType.Vip,
                RetryCount = retryCount
            };
        }

        private void CreateInvoice(int amountSats, string memo, Action<InvoiceResponse> callback)
        {
            string url = $"{config.BaseUrl}/api/v1/payments";

            var requestBody = new
            {
                @out = false,
                amount = amountSats,
                memo = memo
            };
            string jsonBody = JsonConvert.SerializeObject(requestBody);

            var headers = new Dictionary<string, string>
            {
                { "X-Api-Key", config.ApiKey },
                { "Content-Type", "application/json" }
            };

            MakeWebRequest(url, jsonBody, (code, response) =>
            {
                if (code != 200 && code != 201)
                {
                    PrintError($"Error creating invoice: HTTP {code}");
                    callback(null);
                    return;
                }

                try
                {
                    var invoiceResponse = JsonConvert.DeserializeObject<InvoiceResponse>(response);
                    callback(invoiceResponse != null && !string.IsNullOrEmpty(invoiceResponse.PaymentHash) ? invoiceResponse : null);
                }
                catch (Exception ex)
                {
                    PrintError($"Failed to deserialize invoice response: {ex.Message}");
                    callback(null);
                }
            }, RequestMethod.POST, headers);
        }

        private string GetPlayerId(IPlayer player)
        {
            var basePlayer = player.Object as BasePlayer;
            return basePlayer != null ? basePlayer.UserIDString : player.Id;
        }

        private void MakeWebRequest(string url, string jsonData, Action<int, string> callback, RequestMethod method = RequestMethod.GET, Dictionary<string, string> headers = null)
        {
            webrequest.Enqueue(url, jsonData, (code, response) =>
            {
                if (string.IsNullOrEmpty(response) && (code < 200 || code >= 300))
                {
                    PrintError($"Web request to {url} returned empty response or HTTP {code}");
                    callback(code, null);
                }
                else
                {
                    callback(code, response);
                }
            }, this, method, headers ?? new Dictionary<string, string> { { "Content-Type", "application/json" } });
        }

        private void ResolveLightningAddress(string lightningAddress, int amountSats, Action<string> callback)
        {
            var parts = lightningAddress.Split('@');
            if (parts.Length != 2)
            {
                PrintError($"Invalid Lightning Address format: {lightningAddress}");
                callback(null);
                return;
            }

            string user = parts[0];
            string domain = parts[1];

            string lnurlEndpoint = $"https://{domain}/.well-known/lnurlp/{user}";

            var headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" }
            };

            MakeWebRequest(lnurlEndpoint, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    PrintError($"Failed to fetch LNURL for {lightningAddress}: HTTP {code}");
                    callback(null);
                    return;
                }

                try
                {
                    var lnurlResponse = JsonConvert.DeserializeObject<LNURLResponse>(response);
                    if (lnurlResponse == null || string.IsNullOrEmpty(lnurlResponse.Callback))
                    {
                        PrintError($"Invalid LNURL response for {lightningAddress}");
                        callback(null);
                        return;
                    }

                    long amountMsat = (long)amountSats * 1000;

                    string callbackUrl = lnurlResponse.Callback;

                    string callbackUrlWithAmount = $"{callbackUrl}?amount={amountMsat}";

                    MakeWebRequest(callbackUrlWithAmount, null, (payCode, payResponse) =>
                    {
                        if (payCode != 200 || string.IsNullOrEmpty(payResponse))
                        {
                            PrintError($"Failed to perform LNURL Pay for {lightningAddress}: HTTP {payCode}");
                            callback(null);
                            return;
                        }

                        try
                        {
                            var payAction = JsonConvert.DeserializeObject<LNURLPayResponse>(payResponse);
                            if (payAction == null || string.IsNullOrEmpty(payAction.Pr))
                            {
                                PrintError($"Invalid LNURL Pay response for {lightningAddress}");
                                callback(null);
                                return;
                            }

                            callback(payAction.Pr);
                        }
                        catch (Exception ex)
                        {
                            PrintError($"Error parsing LNURL Pay response: {ex.Message}");
                            callback(null);
                        }
                    }, RequestMethod.GET, headers);
                }
                catch (Exception ex)
                {
                    PrintError($"Error parsing LNURL response: {ex.Message}");
                    callback(null);
                }
            }, RequestMethod.GET, headers);
        }

        private class LNURLResponse
        {
            [JsonProperty("tag")]
            public string Tag { get; set; }

            [JsonProperty("callback")]
            public string Callback { get; set; }

            [JsonProperty("minSendable")]
            public long MinSendable { get; set; }

            [JsonProperty("maxSendable")]
            public long MaxSendable { get; set; }

            [JsonProperty("metadata")]
            public string Metadata { get; set; }

            [JsonProperty("commentAllowed")]
            public int CommentAllowed { get; set; }

            [JsonProperty("allowsNostr")]
            public bool AllowsNostr { get; set; }

            [JsonProperty("nostrPubkey")]
            public string NostrPubkey { get; set; }
        }

        private class LNURLPayResponse
        {
            [JsonProperty("pr")]
            public string Pr { get; set; }

            [JsonProperty("routes")]
            public List<object> Routes { get; set; }
        }

        private string ExtractLightningAddress(string memo)
        {
            // Extract the Lightning Address from the memo
            // Expected format: "Sending {amount} {currency} to {lightning_address}"
            var parts = memo.Split(" to ");
            return parts.Length == 2 ? parts[1] : "unknown@unknown.com";
        }
    }
}
