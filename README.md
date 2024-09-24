## Overview:
The **Orangemart** plugin allows players on your Rust server to buy and sell in-game units and VIP status using Bitcoin payments through the Lightning Network. This plugin integrates LNBits into the game, enabling secure transactions for game items and services.

---

## Features

- **In-Game Currency Purchase:** Players can purchase in-game currency using Bitcoin payments.
- **Send In-Game Currency:** Players can send currency to others, facilitating peer-to-peer transactions.
- **VIP Status Purchase:** Players can purchase VIP status through Bitcoin payments, unlocking special privileges.
- **Configurable:** Server admins can set up command names, currency items, prices, and more through the configuration file.

---

## Commands

The following commands are available to players:

- **`/buyblood`**  
  Players can purchase in-game currency using Bitcoin. The amount purchased is configurable.

- **`/sendblood <amount> <targetPlayer>`**  
  Players can send a specified amount of in-game currency to another player.

- **`/buyvip`**  
  Players can purchase VIP status using Bitcoin. The VIP price and associated permission group are configurable.

---

## Configuration

Below is a list of key configuration variables that can be customized in the plugin:

- **`CurrencyItemID`**  
  The item ID used for in-game currency transactions.

- **`BuyCurrencyCommandName`**  
  The name of the command players use to buy in-game currency.

- **`SendCurrencyCommandName`**  
  The name of the command players use to send in-game currency to other players.

- **`BuyVipCommandName`**  
  The name of the command players use to purchase VIP status.

- **`VipPrice`**  
  The price (in satoshis) for players to purchase VIP status.

- **`VipPermissionGroup`**  
  The Oxide permission group that VIP players are added to.

- **`CurrencyName`**  
  The name of the in-game currency.

- **`SatsPerCurrencyUnit`**  
  The conversion rate between satoshis and in-game currency units.

- **`PricePerCurrencyUnit`**  
  The price (in satoshis) per unit of in-game currency.

- **`CheckIntervalSeconds`**  
  Interval time (in seconds) for checking pending Bitcoin transactions.

---

## Installation

1. **Download the Plugin**  
   Place the `Orangemart.cs` file in your server's `oxide/plugins` folder.

2. **Configuration**  
   Modify the plugin’s configuration file to fit your server’s settings (currency item, prices, VIP group, etc.). The configuration file will be automatically generated upon running the plugin for the first time.

3. **Create VIP Group (Optional)**  
   Create a VIP group to assign permssions to.
   
4. **Reload the Plugin**  
   Once configured, reload the plugin using the command:  
   ```  
   oxide.reload Orangemart  
   ```

---

## Permissions

The plugin uses the following permissions:

- **`orangemart.buycurrency`**  
  Grants permission to players who are allowed to buy your currency item via Bitcoin.

- **`orangemart.sendcurrency`**  
  Grants permission to players who are allowed to send Bitcoin for your in-game currency unit.

- **`orangemart.buyvip`**  
  Grants permission to players to purchase VIP via Bitcoin.

---

## Logging and Troubleshooting

- **Logs:**  
  Transaction details, such as purchases and currency sends, are logged for auditing purposes. Logs can be found in the `oxide/data/Orangemart` directory.

- **Troubleshooting:**  
  If any issues arise, check the server logs for errors related to the plugin. Ensure that the configuration file is correctly set up and that Bitcoin payment services are running as expected.

---

## License

This plugin is licensed under the MIT License.
