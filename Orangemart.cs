using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Orangemart", "saulteafarmer", "0.2.0")]
    [Description("Allows players to buy and sell in-game units and VIP status using Bitcoin Lightning Network payments")]
    public class Orangemart : CovalencePlugin
    {
        // Configuration variables
        private int CurrencyItemID;
        private string BuyCurrencyCommandName;
        private string SendCurrencyCommandName;
        private string BuyVipCommandName;
        private int VipPrice;
        private string VipPermissionGroup;
        private string CurrencyName;
        private int SatsPerCurrencyUnit;
        private int PricePerCurrencyUnit;
        private string DiscordChannelName; // Added missing configuration variable
        private ulong CurrencySkinID;

        // Transaction timing settings (moved to config)
        private int CheckIntervalSeconds;
        private int InvoiceTimeoutSeconds;
        private int RetryDelaySeconds;
        private int MaxRetries;

        // Blacklisted domains
        private List<string> BlacklistedDomains = new List<string>();

        // File names
        private const string SellLogFile = "Orangemart/sell_log.json";
        private const string BuyInvoiceLogFile = "Orangemart/buy_invoices.json";

        // LNDHub configuration
        private LNDHubConfig config;
        private string authToken;

        private List<PendingInvoice> pendingInvoices = new List<PendingInvoice>();
        private Dictionary<string, int> retryCounts = new Dictionary<string, int>();

        // Added for handling 404-specific retries
        private Dictionary<string, int> retryCounts404 = new Dictionary<string, int>();
        private const int Max404Retries = 5;
        private const int RetryDelaySeconds404 = 5;

        private class LNDHubConfig
        {
            public string Username { get; set; }
            public string Password { get; set; }
            public string BaseUrl { get; set; }
            public string DiscordWebhookUrl { get; set; }

            public static LNDHubConfig ParseLNDHubConnection(string connectionString)
            {
                try
                {
                    var withoutScheme = connectionString.Replace("lndhub://", "");
                    var parts = withoutScheme.Split('@');
                    if (parts.Length != 2)
                        throw new Exception("Invalid connection string format.");

                    var userInfoPart = parts[0];
                    var baseUrlPart = parts[1];

                    var userInfo = userInfoPart.Split(':');
                    if (userInfo.Length != 2)
                        throw new Exception("Invalid user info in connection string.");

                    var username = userInfo[0];
                    var password = userInfo[1];

                    var baseUrl = baseUrlPart.TrimEnd('/');
                    if (!Uri.IsWellFormedUriString(baseUrl, UriKind.Absolute))
                        throw new Exception("Invalid base URL in connection string.");

                    return new LNDHubConfig
                    {
                        Username = username,
                        Password = password,
                        BaseUrl = baseUrl
                    };
                }
                catch (Exception ex)
                {
                    throw new Exception($"Error parsing LNDHub connection string: {ex.Message}");
                }
            }
        }

        private class AuthResponse
        {
            [JsonProperty("access_token")]
            public string AccessToken { get; set; }

            [JsonProperty("refresh_token")]
            public string RefreshToken { get; set; }
        }

        private class InvoiceResponse
        {
            [JsonProperty("payment_request")]
            public string PaymentRequest { get; set; }

            [JsonProperty("r_hash")]
            public RHashData RHash { get; set; }

            public class RHashData
            {
                [JsonProperty("data")]
                public byte[] Data { get; set; }
            }
        }

        private class SellInvoiceLogEntry
        {
            public string SteamID { get; set; }
            public string LightningAddress { get; set; }
            public bool Success { get; set; }
            public int SatsAmount { get; set; } // Log the amount of sats sent
            public int Fee { get; set; } // Log the fee
            public int FeeMsat { get; set; } // Log the fee in millisatoshis
            public string PaymentRequest { get; set; } // Log the BOLT11 payment request
            public string PaymentHash { get; set; } // Log the payment hash
            public bool CurrencyReturned { get; set; } // Indicates if currency was returned
            public DateTime Timestamp { get; set; }
        }

        private class BuyInvoiceLogEntry
        {
            public string SteamID { get; set; }
            public string InvoiceID { get; set; }
            public bool IsPaid { get; set; }
            public DateTime Timestamp { get; set; }
            public int Amount { get; set; }
            public bool CurrencyGiven { get; set; } // For currency purchases
            public bool VipGranted { get; set; } // For VIP purchases
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
            Vip
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                // Check if config is loaded properly
                if (Config == null || !Config.Exists())
                {
                    PrintError("Configuration file is missing or empty. Creating default configuration.");
                    LoadDefaultConfig();
                    SaveConfig();
                }

                // Access LNDHubConnection from the InvoiceSettings section
                string lndhubConnectionString = (Config["InvoiceSettings"] as Dictionary<string, object>)?["LNDHubConnection"]?.ToString();
                string discordWebhookUrl = (Config["Discord"] as Dictionary<string, object>)?["DiscordWebhookUrl"]?.ToString();

                if (string.IsNullOrEmpty(lndhubConnectionString))
                {
                    PrintError("LNDHubConnection is not set in the configuration file.");
                    return;
                }

                config = LNDHubConfig.ParseLNDHubConnection(lndhubConnectionString);
                config.DiscordWebhookUrl = discordWebhookUrl;

                // Load other configuration settings...
                var currencySettings = Config["CurrencySettings"] as Dictionary<string, object>;
                CurrencyItemID = Convert.ToInt32(currencySettings?["CurrencyItemID"] ?? 1776460938);
                CurrencyName = currencySettings?["CurrencyName"]?.ToString() ?? "blood";
                SatsPerCurrencyUnit = Convert.ToInt32(currencySettings?["SatsPerCurrencyUnit"] ?? 1);
                PricePerCurrencyUnit = Convert.ToInt32(currencySettings?["PricePerCurrencyUnit"] ?? 1);
                CurrencySkinID = (ulong)Convert.ToInt64(currencySettings?["CurrencySkinID"] ?? 0);

                var commandsSettings = Config["Commands"] as Dictionary<string, object>;
                BuyCurrencyCommandName = commandsSettings?["BuyCurrencyCommandName"]?.ToString() ?? "buyblood";
                SendCurrencyCommandName = commandsSettings?["SendCurrencyCommandName"]?.ToString() ?? "sendblood";
                BuyVipCommandName = commandsSettings?["BuyVipCommandName"]?.ToString() ?? "buyvip";

                var vipSettings = Config["VIPSettings"] as Dictionary<string, object>;
                VipPrice = Convert.ToInt32(vipSettings?["VipPrice"] ?? 1000);
                VipPermissionGroup = vipSettings?["VipPermissionGroup"]?.ToString() ?? "vip";

                // Load DiscordChannelName from Discord section
                DiscordChannelName = (Config["Discord"] as Dictionary<string, object>)?["DiscordChannelName"]?.ToString() ?? "mart";

                // Load invoice settings
                var invoiceSettings = Config["InvoiceSettings"] as Dictionary<string, object>;
                CheckIntervalSeconds = Convert.ToInt32(invoiceSettings?["CheckIntervalSeconds"] ?? 10);
                InvoiceTimeoutSeconds = Convert.ToInt32(invoiceSettings?["InvoiceTimeoutSeconds"] ?? 300);
                RetryDelaySeconds = Convert.ToInt32(invoiceSettings?["RetryDelaySeconds"] ?? 10);
                MaxRetries = Convert.ToInt32(invoiceSettings?["MaxRetries"] ?? 25);
                BlacklistedDomains = (invoiceSettings?["BlacklistedDomains"] as List<object>)?.ConvertAll(d => d.ToString().ToLower()) ?? new List<string>();
            }
            catch (Exception ex)
            {
                PrintError($"Failed to load configuration: {ex.Message}");
            }
        }

        protected override void LoadDefaultConfig()
        {
            Config["Commands"] = new Dictionary<string, object>
            {
                ["BuyCurrencyCommandName"] = "buyblood",
                ["BuyVipCommandName"] = "buyvip",
                ["SendCurrencyCommandName"] = "sendblood"
            };

            Config["Discord"] = new Dictionary<string, object>
            {
                ["DiscordChannelName"] = "mart",
                ["DiscordWebhookUrl"] = "https://discord.com/api/webhooks/your_webhook_url"
            };

            Config["InvoiceSettings"] = new Dictionary<string, object>
            {
                ["BlacklistedDomains"] = new List<string> { "example.com", "blacklisted.net" },
                ["CheckIntervalSeconds"] = 10,
                ["InvoiceTimeoutSeconds"] = 300,
                ["LNDHubConnection"] = "lndhub://admin:password@sats.love/",
                ["MaxRetries"] = 25,
                ["RetryDelaySeconds"] = 10
            };

            Config["VIPSettings"] = new Dictionary<string, object>
            {
                ["VipPermissionGroup"] = "vip",
                ["VipPrice"] = 1000
            };

            Config["CurrencySettings"] = new Dictionary<string, object>
            {
                ["CurrencyItemID"] = 1776460938,
                ["CurrencyName"] = "blood",
                ["PricePerCurrencyUnit"] = 1,
                ["SatsPerCurrencyUnit"] = 1,
                ["CurrencySkinID"] = 0
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

            // Register commands dynamically
            AddCovalenceCommand(BuyCurrencyCommandName, nameof(CmdBuyCurrency), "orangemart.buycurrency");
            AddCovalenceCommand(SendCurrencyCommandName, nameof(CmdSendCurrency), "orangemart.sendcurrency");
            AddCovalenceCommand(BuyVipCommandName, nameof(CmdBuyVip), "orangemart.buyvip");

            timer.Every(CheckIntervalSeconds, CheckPendingInvoices);
        }

        private void Unload()
        {
            pendingInvoices.Clear();
            retryCounts.Clear();
            retryCounts404.Clear(); // Clear 404-specific retries
            authToken = null;
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["UsageSendCurrency"] = "Usage: /{0} <amount> <lightning_address>",
                ["NeedMoreCurrency"] = "You need more {0}. You currently have {1}.",
                ["FailedToReserveCurrency"] = "Failed to reserve currency. Please try again.",
                ["FailedToQueryLightningAddress"] = "Failed to query Lightning address for an invoice.",
                ["FailedToAuthenticate"] = "Failed to authenticate with LNDHub.",
                ["InvoiceCreatedCheckDiscord"] = "Invoice created! Please check the #{0} channel on Discord to complete your payment.",
                ["FailedToCreateInvoice"] = "Failed to create an invoice. Please try again later.",
                ["FailedToProcessPayment"] = "Failed to process payment.",
                ["CurrencySentSuccess"] = "You have successfully sent {0} {1}!",
                ["PurchaseSuccess"] = "You have successfully purchased {0} {1}!",
                ["PurchaseVipSuccess"] = "You have successfully purchased VIP status!",
                ["InvalidCommandUsage"] = "Usage: /{0} <amount>",
                ["NoPermission"] = "You do not have permission to use this command.",
                ["FailedToFindBasePlayer"] = "Failed to find base player object for player {0}.",
                ["FailedToCreateCurrencyItem"] = "Failed to create {0} item for player {1}.",
                ["AddedToVipGroup"] = "Player {0} added to VIP group '{1}'.",
                ["InvoiceExpired"] = "Your invoice for {0} sats has expired. Please try again.",
                ["BlacklistedDomain"] = "The domain '{0}' is currently blacklisted. Please use a different Lightning address."
            }, this);
        }

        private string Lang(string key, string userId = null, params object[] args)
        {
            return string.Format(lang.GetMessage(key, this, userId), args);
        }

        private void CheckPendingInvoices()
        {
            foreach (var invoice in pendingInvoices.ToArray())
            {
                CheckInvoicePaid(invoice.RHash, (isPaid, isPending) =>
                {
                    if (isPaid)
                    {
                        pendingInvoices.Remove(invoice);

                        if (invoice.Type == PurchaseType.Currency)
                        {
                            RewardPlayer(invoice.Player, invoice.Amount);
                        }
                        else if (invoice.Type == PurchaseType.Vip)
                        {
                            GrantVip(invoice.Player);
                        }

                        var logEntry = new BuyInvoiceLogEntry
                        {
                            SteamID = invoice.Player.Id,
                            InvoiceID = invoice.RHash,
                            IsPaid = true,
                            Timestamp = DateTime.UtcNow,
                            Amount = invoice.Amount,
                            CurrencyGiven = invoice.Type == PurchaseType.Currency,
                            VipGranted = invoice.Type == PurchaseType.Vip
                        };
                        LogBuyInvoice(logEntry);
                    }
                    else if (!isPending)
                    {
                        if (!retryCounts.ContainsKey(invoice.RHash))
                        {
                            retryCounts[invoice.RHash] = 0;
                        }

                        retryCounts[invoice.RHash]++;
                        if (retryCounts[invoice.RHash] >= MaxRetries)
                        {
                            pendingInvoices.Remove(invoice);
                            retryCounts.Remove(invoice.RHash);
                            PrintWarning($"Invoice for player {invoice.Player.Id} expired (amount: {invoice.Amount} sats).");

                            // Notify the player about the expired invoice
                            invoice.Player.Reply(Lang("InvoiceExpired", invoice.Player.Id, invoice.Amount));

                            var logEntry = new BuyInvoiceLogEntry
                            {
                                SteamID = invoice.Player.Id,
                                InvoiceID = invoice.RHash,
                                IsPaid = false,
                                Timestamp = DateTime.UtcNow,
                                Amount = invoice.Amount,
                                CurrencyGiven = false,
                                VipGranted = false
                            };
                            LogBuyInvoice(logEntry);
                        }
                        else
                        {
                            PrintWarning($"Retrying invoice {invoice.RHash}. Attempt {retryCounts[invoice.RHash]} of {MaxRetries}.");
                        }
                    }
                });
            }
        }

        private void CheckInvoicePaid(string rHash, Action<bool, bool> callback)
        {
            if (string.IsNullOrEmpty(authToken))
            {
                GetAuthToken(token =>
                {
                    if (!string.IsNullOrEmpty(token))
                    {
                        CheckInvoicePaid(rHash, callback); // Retry after getting token
                    }
                    else
                    {
                        callback(false, false);
                    }
                });
                return;
            }

            string baseUrlForCheckingInvoice = config.BaseUrl.Replace("/lndhub/ext", ""); // Remove any "/lndhub/ext" part
            string url = $"{baseUrlForCheckingInvoice}/api/v1/payments/{rHash}";

            PrintWarning($"Checking invoice at URL: {url}");
            PrintWarning($"rHash being checked: {rHash}");

            var headers = new Dictionary<string, string>
            {
                { "X-Api-Key", authToken },
                { "Content-Type", "application/json" }
            };

            webrequest.Enqueue(url, null, (code, response) =>
            {
                if (code == 404)
                {
                    PrintError($"Error checking invoice status: HTTP {code} (Not Found)");
                    PrintWarning($"Ensure the correct rHash: {rHash}");

                    // Initialize retry count for 404 if not present
                    if (!retryCounts404.ContainsKey(rHash))
                    {
                        retryCounts404[rHash] = 1;
                    }
                    else
                    {
                        retryCounts404[rHash]++;
                    }

                    // Check if retry count has exceeded the maximum allowed retries (5)
                    if (retryCounts404[rHash] <= Max404Retries)
                    {
                        PrintWarning($"Retrying invoice {rHash}. Attempt {retryCounts404[rHash]} of {Max404Retries}.");

                        // Schedule a retry after 5 seconds
                        timer.Once(RetryDelaySeconds404, () =>
                        {
                            CheckInvoicePaid(rHash, callback);
                        });
                    }
                    else
                    {
                        PrintWarning($"Max retries reached for invoice {rHash}. Marking as failed.");

                        // Remove the retry count as we've exceeded the max retries
                        retryCounts404.Remove(rHash);

                        // Optionally, notify the player about the failed payment
                        var invoice = pendingInvoices.Find(inv => inv.RHash == rHash);
                        if (invoice != null)
                        {
                            pendingInvoices.Remove(invoice);
                            invoice.Player.Reply(Lang("InvoiceExpired", invoice.Player.Id, invoice.Amount));

                            var logEntry = new BuyInvoiceLogEntry
                            {
                                SteamID = invoice.Player.Id,
                                InvoiceID = invoice.RHash,
                                IsPaid = false,
                                Timestamp = DateTime.UtcNow,
                                Amount = invoice.Amount,
                                CurrencyGiven = invoice.Type == PurchaseType.Currency,
                                VipGranted = invoice.Type == PurchaseType.Vip
                            };
                            LogBuyInvoice(logEntry);
                        }

                        // Finally, mark the payment as failed
                        callback(false, false);
                    }
                    return;
                }

                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    PrintError($"Error checking invoice status: HTTP {code}");
                    callback(false, false);
                    return;
                }

                try
                {
                    var jsonResponse = JsonConvert.DeserializeObject<Dictionary<string, object>>(response);

                    if (jsonResponse != null && jsonResponse.ContainsKey("paid"))
                    {
                        bool isPaid = Convert.ToBoolean(jsonResponse["paid"]);
                        bool isPending = jsonResponse.ContainsKey("status") && jsonResponse["status"].ToString().ToLower() == "pending";

                        if (isPaid)
                        {
                            // Remove retry counts (both general and 404-specific) on successful payment
                            if (retryCounts.ContainsKey(rHash))
                            {
                                retryCounts.Remove(rHash);
                            }

                            if (retryCounts404.ContainsKey(rHash))
                            {
                                retryCounts404.Remove(rHash);
                            }

                            callback(true, false);
                        }
                        else if (isPending)
                        {
                            callback(false, true);
                        }
                        else
                        {
                            // For other statuses, you might want to handle them differently
                            callback(false, false);
                        }
                    }
                    else
                    {
                        callback(false, false);
                    }
                }
                catch (Exception ex)
                {
                    PrintError($"Failed to parse invoice status response: {ex.Message}");
                    callback(false, false);
                }
            }, this, RequestMethod.GET, headers);
        }

        private void GetAuthToken(Action<string> callback)
        {
            if (!string.IsNullOrEmpty(authToken))
            {
                callback(authToken);
                return;
            }

            string url = $"{config.BaseUrl}/auth";
            Puts($"Attempting to authenticate with URL: {url}");

            var requestBody = new
            {
                login = config.Username,
                password = config.Password
            };
            string jsonBody = JsonConvert.SerializeObject(requestBody);

            var headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" }
            };

            webrequest.Enqueue(url, jsonBody, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    PrintError($"Error getting auth token: HTTP {code}");
                    PrintError($"Response: {response}");
                    callback(null);
                    return;
                }

                try
                {
                    var authResponse = JsonConvert.DeserializeObject<AuthResponse>(response);
                    if (authResponse == null || string.IsNullOrEmpty(authResponse.AccessToken))
                    {
                        PrintError("Invalid auth response.");
                        PrintError($"Response: {response}");
                        callback(null);
                        return;
                    }

                    authToken = authResponse.AccessToken;
                    callback(authToken);
                }
                catch (Exception ex)
                {
                    PrintError($"Failed to parse auth response: {ex.Message}");
                    PrintError($"Raw response: {response}");
                    callback(null);
                }
            }, this, RequestMethod.POST, headers);
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
                player.Reply(Lang("UsageSendCurrency", player.Id, SendCurrencyCommandName));
                return;
            }

            string lightningAddress = args[1];

            // Check if the Lightning address is from a blacklisted domain
            if (IsLightningAddressBlacklisted(lightningAddress))
            {
                string domain = GetDomainFromLightningAddress(lightningAddress);
                player.Reply(Lang("BlacklistedDomain", player.Id, domain));
                return;
            }

            var basePlayer = player.Object as BasePlayer;
            if (basePlayer == null)
            {
                player.Reply(Lang("FailedToFindBasePlayer", player.Id, player.Name));
                return;
            }

            int currencyAmount = 0;

            // If CurrencySkinID is defined and greater than 0, only check for items with that skin ID
            if (CurrencySkinID > 0)
            {
                var itemsWithSkin = basePlayer.inventory.FindItemsByItemID(CurrencyItemID);
                foreach (var item in itemsWithSkin)
                {
                    if (item.skin == CurrencySkinID)
                    {
                        currencyAmount += item.amount;
                    }
                }

                // If no matching items were found, inform the player and stop
                if (currencyAmount == 0)
                {
                    player.Reply($"You do not have any {CurrencyName} with the required skin ID.");
                    return;
                }
            }
            else
            {
                // Check for all items with the given CurrencyItemID regardless of skin
                currencyAmount = basePlayer.inventory.GetAmount(CurrencyItemID);
            }

            if (currencyAmount < amount)
            {
                player.Reply(Lang("NeedMoreCurrency", player.Id, CurrencyName, currencyAmount));
                return;
            }

            // Reserve the currency by immediately removing it from the player's inventory
            int reservedAmount = ReserveCurrencyWithSkin(basePlayer, amount);
            if (reservedAmount == 0)
            {
                player.Reply(Lang("FailedToReserveCurrency", player.Id));
                return;
            }

            GetAuthToken(token =>
            {
                if (!string.IsNullOrEmpty(token))
                {
                    QueryLightningAddressForInvoice(lightningAddress, reservedAmount * SatsPerCurrencyUnit, invoiceUrl =>
                    {
                        if (string.IsNullOrEmpty(invoiceUrl))
                        {
                            // If the invoice query fails, return the currency
                            ReturnCurrency(basePlayer, reservedAmount);
                            player.Reply(Lang("FailedToQueryLightningAddress", player.Id));
                            LogSellTransaction(player.Id, lightningAddress, false, reservedAmount * SatsPerCurrencyUnit, 0, 0, null, null, true);
                            return;
                        }

                        SendPayment(invoiceUrl, token, reservedAmount * SatsPerCurrencyUnit, player, lightningAddress, success =>
                        {
                            if (success)
                            {
                                // Do nothing further as the currency is already removed on success
                                player.Reply(Lang("CurrencySentSuccess", player.Id, reservedAmount, CurrencyName));
                            }
                            else
                            {
                                // If the payment fails, return the reserved currency
                                ReturnCurrency(basePlayer, reservedAmount);
                                player.Reply(Lang("FailedToProcessPayment", player.Id));
                                LogSellTransaction(player.Id, lightningAddress, false, reservedAmount * SatsPerCurrencyUnit, 0, 0, null, null, true);
                            }
                        });
                    });
                }
                else
                {
                    // If authentication fails, return the currency
                    ReturnCurrency(basePlayer, reservedAmount);
                    player.Reply(Lang("FailedToAuthenticate", player.Id));
                    LogSellTransaction(player.Id, lightningAddress, false, reservedAmount * SatsPerCurrencyUnit, 0, 0, null, null, true);
                }
            });
        }

        // Helper method to reserve currency with the specific skin ID
        private int ReserveCurrencyWithSkin(BasePlayer player, int amount)
        {
            var items = player.inventory.FindItemsByItemID(CurrencyItemID);
            int remaining = amount;
            int reserved = 0;

            foreach (var item in items)
            {
                // Check if the item matches the defined CurrencySkinID, if applicable
                if (CurrencySkinID > 0 && item.skin != CurrencySkinID)
                {
                    continue; // Skip items without the defined skin ID
                }

                if (item.amount > remaining)
                {
                    item.UseItem(remaining);
                    reserved += remaining;
                    remaining = 0;
                    break;
                }
                else
                {
                    reserved += item.amount;
                    remaining -= item.amount;
                    item.Remove();
                }
            }

            if (remaining > 0)
            {
                // Rollback if unable to remove the full amount
                PrintWarning($"Could not reserve the full amount of {CurrencyName}. {remaining} remaining.");
                ReturnCurrency(player, reserved); // Return whatever was taken
                return 0; // Indicate failure to reserve
            }

            return reserved; // Return the amount actually reserved
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
                player.Reply(Lang("InvalidCommandUsage", player.Id, BuyCurrencyCommandName));
                return;
            }

            int amountSats = amount * PricePerCurrencyUnit;

            CreateInvoice(amountSats, $"Buying {amount} {CurrencyName}", invoiceResponse =>
            {
                if (invoiceResponse != null)
                {
                    // Send the invoice via Discord webhook
                    SendInvoiceToDiscord(player, invoiceResponse.PaymentRequest, amountSats, $"Buying {amount} {CurrencyName}");

                    // Notify the player in chat to check the Discord channel
                    player.Reply(Lang("InvoiceCreatedCheckDiscord", player.Id, DiscordChannelName));

                    // Add the pending invoice
                    var pendingInvoice = new PendingInvoice
                    {
                        RHash = BitConverter.ToString(invoiceResponse.RHash.Data).Replace("-", "").ToLower(),
                        Player = player,
                        Amount = amount,
                        Memo = $"Buying {amount} {CurrencyName}",
                        CreatedAt = DateTime.UtcNow,
                        Type = PurchaseType.Currency
                    };
                    pendingInvoices.Add(pendingInvoice);

                    // Start checking the invoice status after a delay
                    timer.Once(RetryDelaySeconds, () =>
                    {
                        CheckPendingInvoices();
                    });

                    // Expire invoice after timeout if unpaid
                    timer.Once(InvoiceTimeoutSeconds, () =>
                    {
                        if (pendingInvoices.Contains(pendingInvoice))
                        {
                            pendingInvoices.Remove(pendingInvoice);
                            PrintWarning($"Invoice for player {player.Id} expired (amount: {amountSats} sats).");

                            var logEntry = new BuyInvoiceLogEntry
                            {
                                SteamID = player.Id,
                                InvoiceID = pendingInvoice.RHash,
                                IsPaid = false,
                                Timestamp = DateTime.UtcNow,
                                Amount = amountSats,
                                CurrencyGiven = false
                            };
                            LogBuyInvoice(logEntry);
                        }
                    });
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

            int amountSats = VipPrice;

            CreateInvoice(amountSats, $"Buying VIP Status", invoiceResponse =>
            {
                if (invoiceResponse != null)
                {
                    // Send the invoice via Discord webhook
                    SendInvoiceToDiscord(player, invoiceResponse.PaymentRequest, amountSats, $"Buying VIP Status");

                    // Notify the player in chat to check the Discord channel
                    player.Reply(Lang("InvoiceCreatedCheckDiscord", player.Id, DiscordChannelName));

                    // Add the pending invoice
                    var pendingInvoice = new PendingInvoice
                    {
                        RHash = BitConverter.ToString(invoiceResponse.RHash.Data).Replace("-", "").ToLower(),
                        Player = player,
                        Amount = amountSats,
                        Memo = $"Buying VIP Status",
                        CreatedAt = DateTime.UtcNow,
                        Type = PurchaseType.Vip
                    };
                    pendingInvoices.Add(pendingInvoice);

                    // Start checking the invoice status after a delay
                    timer.Once(RetryDelaySeconds, () =>
                    {
                        CheckPendingInvoices();
                    });

                    // Expire invoice after timeout if unpaid
                    timer.Once(InvoiceTimeoutSeconds, () =>
                    {
                        if (pendingInvoices.Contains(pendingInvoice))
                        {
                            pendingInvoices.Remove(pendingInvoice);
                            PrintWarning($"VIP purchase invoice for player {player.Id} expired.");

                            var logEntry = new BuyInvoiceLogEntry
                            {
                                SteamID = player.Id,
                                InvoiceID = pendingInvoice.RHash,
                                IsPaid = false,
                                Timestamp = DateTime.UtcNow,
                                Amount = amountSats,
                                VipGranted = false
                            };
                            LogBuyInvoice(logEntry);
                        }
                    });
                }
                else
                {
                    player.Reply(Lang("FailedToCreateInvoice", player.Id));
                }
            });
        }

        private bool IsLightningAddressBlacklisted(string lightningAddress)
        {
            string domain = GetDomainFromLightningAddress(lightningAddress);
            if (string.IsNullOrEmpty(domain))
                return false;

            return BlacklistedDomains.Contains(domain.ToLower());
        }

        private string GetDomainFromLightningAddress(string lightningAddress)
        {
            if (string.IsNullOrEmpty(lightningAddress))
                return null;

            var parts = lightningAddress.Split('@');
            if (parts.Length != 2)
                return null;

            return parts[1].ToLower();
        }

        private void QueryLightningAddressForInvoice(string lightningAddress, int satsAmount, Action<string> callback)
        {
            string[] parts = lightningAddress.Split('@');
            if (parts.Length != 2)
            {
                PrintError($"Invalid Lightning address: {lightningAddress}");
                callback(null);
                return;
            }

            string username = parts[0];
            string domain = parts[1];
            string lnurlUrl = $"https://{domain}/.well-known/lnurlp/{username}";

            Puts($"Querying Lightning address: {lightningAddress} for {satsAmount} sat invoice at URL: {lnurlUrl}");

            webrequest.Enqueue(lnurlUrl, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    PrintError($"Error querying LNURL: HTTP {code}");
                    PrintError($"Response: {response}");
                    callback(null);
                    return;
                }

                try
                {
                    var lnurlResponse = JsonConvert.DeserializeObject<Dictionary<string, object>>(response);

                    if (lnurlResponse.ContainsKey("status") && lnurlResponse["status"].ToString() == "ERROR")
                    {
                        PrintError($"Error from LNURL provider: {lnurlResponse["reason"]}");
                        callback(null);
                        return;
                    }

                    string callbackUrl = lnurlResponse["callback"].ToString();
                    callback($"{callbackUrl}?amount={satsAmount * 1000}"); // Amount in millisatoshis
                }
                catch (Exception ex)
                {
                    PrintError($"Failed to parse LNURL response: {ex.Message}");
                    callback(null);
                }
            }, this, RequestMethod.GET);
        }

        private void SendPayment(string invoiceUrl, string token, int satsAmount, IPlayer player, string lightningAddress, Action<bool> callback)
        {
            Puts($"Fetching invoice from: {invoiceUrl}");

            webrequest.Enqueue(invoiceUrl, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    PrintError($"Error fetching invoice: HTTP {code}");
                    PrintError($"Response: {response}");
                    callback(false);
                    return;
                }

                try
                {
                    var invoiceResponse = JsonConvert.DeserializeObject<Dictionary<string, object>>(response);

                    if (invoiceResponse.ContainsKey("pr"))
                    {
                        string paymentRequest = invoiceResponse["pr"].ToString();
                        Puts($"Fetched payment request: {paymentRequest}");

                        ProcessPayment(paymentRequest, token, satsAmount, player, lightningAddress, callback);
                    }
                    else
                    {
                        PrintError("Invalid invoice response from LNURL provider");
                        callback(false);
                    }
                }
                catch (Exception ex)
                {
                    PrintError($"Failed to parse invoice response: {ex.Message}");
                    callback(false);
                }
            }, this, RequestMethod.GET);
        }

        private void ProcessPayment(string paymentRequest, string token, int satsAmount, IPlayer player, string lightningAddress, Action<bool> callback)
        {
            string url = $"{config.BaseUrl}/payinvoice";

            var requestBody = new
            {
                invoice = paymentRequest,
            };
            string jsonBody = JsonConvert.SerializeObject(requestBody);

            var headers = new Dictionary<string, string>
            {
                { "Authorization", $"Bearer {token}" },
                { "Content-Type", "application/json" }
            };

            Puts($"Payment request URL: {url}");
            Puts($"Payment request headers: {string.Join(", ", headers)}");

            webrequest.Enqueue(url, jsonBody, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    PrintError($"Error processing payment: HTTP {code}");
                    PrintError($"Response: {response}");
                    callback(false);
                    return;
                }

                try
                {
                    // Log the raw response before parsing
                    Puts($"Raw payment response: {response}");

                    var paymentResponse = JsonConvert.DeserializeObject<Dictionary<string, object>>(response);

                    // Check for errors in the response
                    if (paymentResponse.ContainsKey("error") && paymentResponse["error"] != null)
                    {
                        PrintError($"Payment failed: {paymentResponse["error"]}");
                        callback(false);
                        return;
                    }

                    // Safely extract and log fee and fee_msat if they exist
                    int fee = 0;
                    if (paymentResponse.ContainsKey("fee") && paymentResponse["fee"] is long)
                    {
                        fee = (int)(long)paymentResponse["fee"]; // Safe cast to handle long types
                    }
                    else if (paymentResponse.ContainsKey("fee"))
                    {
                        Puts($"Unexpected fee type: {paymentResponse["fee"]?.GetType()}");
                    }

                    int feeMsat = 0;
                    if (paymentResponse.ContainsKey("fee_msat") && paymentResponse["fee_msat"] is long)
                    {
                        feeMsat = (int)(long)paymentResponse["fee_msat"]; // Safe cast to handle long types
                    }
                    else if (paymentResponse.ContainsKey("fee_msat"))
                    {
                        Puts($"Unexpected fee_msat type: {paymentResponse["fee_msat"]?.GetType()}");
                    }

                    string paymentHash = paymentResponse.ContainsKey("payment_hash") ? paymentResponse["payment_hash"].ToString() : "unknown";

                    Puts($"Payment processed successfully. Fee: {fee} msats, Fee (msat): {feeMsat}, Payment Hash: {paymentHash}");

                    // Log the successful transaction
                    LogSellTransaction(player.Id, lightningAddress, true, satsAmount, fee, feeMsat, paymentRequest, paymentHash, false);
                    callback(true);
                }
                catch (Exception ex)
                {
                    PrintError($"Failed to parse payment response: {ex.Message}");
                    PrintError($"Raw response: {response}");
                    callback(false);
                }
            }, this, RequestMethod.POST, headers);
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
                        description = $"{memo}\n\nPlease pay the following Lightning invoice to complete your purchase:\n\n```{invoice}```",
                        image = new
                        {
                            url = qrCodeUrl
                        },
                        fields = new[]
                        {
                            new
                            {
                                name = "Amount",
                                value = $"{amountSats} sats",
                                inline = true
                            },
                            new
                            {
                                name = "Steam ID",
                                value = player.Id,
                                inline = true
                            }
                        }
                    }
                }
            };

            string jsonPayload = JsonConvert.SerializeObject(webhookPayload);
            webrequest.Enqueue(config.DiscordWebhookUrl, jsonPayload, (code, response) =>
            {
                if (code != 204)
                {
                    PrintError($"Failed to send invoice to Discord webhook: HTTP {code}");
                }
                else
                {
                    Puts($"Invoice sent to Discord for player {player.Id}.");
                }
            }, this, RequestMethod.POST, new Dictionary<string, string> { { "Content-Type", "application/json" } });
        }

        private void RewardPlayer(IPlayer player, int amount)
        {
            player.Reply($"You have successfully purchased {amount} {CurrencyName}!");

            var basePlayer = player.Object as BasePlayer;

            if (basePlayer != null)
            {
                var currencyItem = ItemManager.CreateByItemID(CurrencyItemID, amount);
                if (currencyItem != null)
                {
                    if (CurrencySkinID > 0) // Check if a skinID is defined and apply it
                    {
                        currencyItem.skin = (ulong)CurrencySkinID;
                    }

                    basePlayer.GiveItem(currencyItem);
                    Puts($"Gave {amount} {CurrencyName} (skinID: {CurrencySkinID}) to player {player.Id}.");
                }
                else
                {
                    PrintError($"Failed to create {CurrencyName} item for player {player.Id}.");
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

            // Add the player to the VIP permission group
            permission.AddUserGroup(player.Id, VipPermissionGroup);

            Puts($"Player {player.Id} added to VIP group '{VipPermissionGroup}'.");
        }

        private int ReserveCurrency(BasePlayer player, int amount)
        {
            var items = player.inventory.FindItemsByItemID(CurrencyItemID);
            int remaining = amount;
            int reserved = 0;

            foreach (var item in items)
            {
                if (item.amount > remaining)
                {
                    item.UseItem(remaining);
                    reserved += remaining;
                    remaining = 0;
                    break;
                }
                else
                {
                    reserved += item.amount;
                    remaining -= item.amount;
                    item.Remove();
                }
            }

            if (remaining > 0)
            {
                // Rollback if unable to remove the full amount
                PrintWarning($"Could not reserve the full amount of {CurrencyName}. {remaining} remaining.");
                ReturnCurrency(player, reserved); // Return whatever was taken
                return 0; // Indicate failure to reserve
            }

            return reserved; // Return the amount actually reserved
        }

        private void ReturnCurrency(BasePlayer player, int amount)
        {
            var returnedCurrency = ItemManager.CreateByItemID(CurrencyItemID, amount);
            if (returnedCurrency != null)
            {
                returnedCurrency.MoveToContainer(player.inventory.containerMain);
            }
        }

        private void LogSellTransaction(string steamID, string lightningAddress, bool success, int satsAmount, int fee, int feeMsat, string paymentRequest, string paymentHash, bool currencyReturned)
        {
            var logEntry = new SellInvoiceLogEntry
            {
                SteamID = steamID,
                LightningAddress = lightningAddress,
                Success = success,
                SatsAmount = satsAmount, // Store sats sent
                Fee = fee, // Store fee
                FeeMsat = feeMsat, // Store fee in millisatoshis
                PaymentRequest = paymentRequest, // Store BOLT11 payment request
                PaymentHash = paymentHash, // Store payment hash
                CurrencyReturned = currencyReturned, // Indicates if currency was returned
                Timestamp = DateTime.UtcNow
            };

            var logs = LoadSellLogData();
            logs.Add(logEntry);
            SaveSellLogData(logs);
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

            List<BuyInvoiceLogEntry> invoiceLogs;

            if (File.Exists(logPath))
            {
                invoiceLogs = JsonConvert.DeserializeObject<List<BuyInvoiceLogEntry>>(File.ReadAllText(logPath)) ?? new List<BuyInvoiceLogEntry>();
            }
            else
            {
                invoiceLogs = new List<BuyInvoiceLogEntry>();
            }

            invoiceLogs.Add(logEntry);
            File.WriteAllText(logPath, JsonConvert.SerializeObject(invoiceLogs, Formatting.Indented));
        }

        private void CreateInvoice(int amountSats, string memo, Action<InvoiceResponse> callback)
        {
            if (string.IsNullOrEmpty(authToken))
            {
                GetAuthToken(token =>
                {
                    if (!string.IsNullOrEmpty(token))
                    {
                        CreateInvoice(amountSats, memo, callback); // Retry after getting token
                    }
                    else
                    {
                        callback(null);
                    }
                });
                return;
            }

            string url = $"{config.BaseUrl}/addinvoice";
            var requestBody = new
            {
                amt = amountSats,
                memo = memo
            };
            string jsonBody = JsonConvert.SerializeObject(requestBody);

            var headers = new Dictionary<string, string>
            {
                { "Authorization", $"Bearer {authToken}" },
                { "Content-Type", "application/json" }
            };

            webrequest.Enqueue(url, jsonBody, (code, response) =>
            {
                PrintWarning($"Raw LNDHub response: {response}");

                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    PrintError($"Error creating invoice: HTTP {code}");
                    callback(null);
                    return;
                }

                try
                {
                    var invoiceResponse = JsonConvert.DeserializeObject<InvoiceResponse>(response);
                    string rHashString = BitConverter.ToString(invoiceResponse.RHash.Data).Replace("-", "").ToLower();
                    PrintWarning($"Parsed r_hash: {rHashString}");

                    callback(invoiceResponse);
                }
                catch (Exception ex)
                {
                    PrintError($"Failed to deserialize invoice response: {ex.Message}");
                }
            }, this, RequestMethod.POST, headers);
        }
    }
}
