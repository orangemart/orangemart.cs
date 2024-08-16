# orangemart.cs

**Exciting Developer Bounty: Create a uMod Plugin for Nostr Wallet Connect Integration!**

We are thrilled to announce an open-source project opportunity to create a plugin that will integrate Nostr Wallet Connect (NWC) into the popular game Rust. This plugin could revolutionize in-game commerce and server monetization, benefiting the entire Rust community, and we need passionate developers like you to bring it to life!

### About the Project
Our goal is to develop a uMod (oxide) plugin that leverages NWC, as outlined in [NIP-47](https://github.com/nostr-protocol/nips/blob/master/47.md) of the Nostr protocol. If you’re new to Nostr, check out our detailed post [here](https://www.orangem.art/blog/nostr) to get started and learn more about the protocol [here](https://nostr.org/). NWC documentation is available [here](https://docs.nwc.dev/), and you can join the NWC developer Discord [here](https://discord.gg/PRhQPZCmeF).

The plugin will be server-side, built using the .NET Framework, and written in C#. Utilizing uMod’s capabilities, the plugin will dynamically enhance game functionality without requiring any client modifications. You can find uMod documentation [here](https://umod.org/documentation/api/overview) and join the uMod/Oxide developer Discord [here](https://discord.gg/HdhSD8aBXD), check out this guide from [@thethingtracks](https://medium.com/@thethingtracks/simple-rust-plugin-template-a0f405da8f64). There is some NWC support in [NNostr](https://github.com/Kukks/NNostr) which is written in C# and could be a useful reference point. 

"The cool thing with NWC is that it's quite easy to implement, no matter which language / tech stack you are using. It's basically sending some (signed & encrypted) JSON messages over a websocket connection." - [@reneaaron](https://stacker.news/items/640244/r/TheOrangeMart?commentId=640348) 

### Initial Use Case
**Server Wallet Integration:**
- The server admin will input the NWC connection pairing secret into the plugin's configuration file.
- The plugin will use this pairing secret to interact with the server's wallet.

**Player-to-Server Commerce Examples:**
1. **Buying VIP Status:**
   - Players type /buyvip in-game.
   - The plugin requests an invoice from the server's wallet using NWC.
   - The invoice is displayed to the player as a QR code. Once paid, the player is added to the oxide permission group granting them VIP perks.
   - The configuration file allows customization of command name, price, and the Oxide permission group granted.

2. **Buying In-Game Currency:**
   - If the currency item is blood, players could type /buyblood and specify the amount they wish to purchase.
   - The plugin requests an invoice from the server's wallet and displays it to the player.
   - Once paid, the player receives the specified quantity of blood (1 blood = 1 sat).
   - The configuration file allows customization of currency item and command name.

3. **Selling In-Game Currency (sending):**
   - Players type /sendblood and provide a destination Lightning address.
   - The plugin deletes the specified amount of blood (currency item) from the player's inventory.
   - Using NWC, the plugin sends sats from the server's wallet to the destination address.

### Future Expansion Ideas
**Player-to-Player Commerce:**
- Enable the plugin to handle P2P commerce, enhancing in-game trading mechanics like the Vending Machine and less reliance on an in-game currency item. Possibly by allowing players to input their NWC connection pairing secret.

**Embedded Wallet:**
- Utilize the Alby Hub's new isolated apps feature to give all players an embedded wallet. Join the Alby discord [here](https://discord.gg/4a79bPPgBW).

### Example Development Tasks:
- Develop the plugin as a C# code file.
- Ensure the plugin generates a JSON configuration file for customization.
- Implement chat commands for buying and selling in-game currency.
- Handle errors and notify players in chat with the lightning invoice.
- Check invoice status and handle unsuccessful payments.

### Why Join Us?
We have set aside **1 million sats ($650 USD at today’s value)** for the initial development and ongoing support of this plugin. Orangemart’s proof of funds can be found at our [Geyser Fundraiser](https://geyser.fund/project/orange), where we have received over **25 million sats** in donations from over 100 contributors. Additionally, proof of our disbursement of these funds to our community is available on the [Lightsats leaderboard](https://lightsats.com/leaderboard), where we have gifted over **10 million sats** in over 7000 prizes to our community.

This is a fantastic opportunity to contribute to an exciting project that could significantly impact the Rust community. Join our Discord to get started and collaborate with us on this thrilling journey: [Orangemart Discord](https://dsc.gg/orangemart).

Let’s build something amazing together! 🚀

Bounty originally published at [orangem.art](https://www.orangem.art/blog/nwcplugin)
