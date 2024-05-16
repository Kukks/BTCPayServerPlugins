# Breez lightning support plugin

## BETA RELEASE

Allows you to enable lightning on your stores using [Breez SDK](https://breez.technology/sdk/), powered by [Blockstream Greenlight](https://blockstream.com/lightning/greenlight/).

Breez SDK and Greenlight enables you to have a non-custodial lightning experience, without hosting any of the infrastructure yourself.

Additionally, Breez SDK comes with built-in liquidity and channel automation, reducing the complexity of managing your lightning node.

If you have used any other wallet that uses Breez SDK, you can import it directly into BTCPay Server and continue using it in parallel.

## Usage

1. Install the plugin from the BTCPay Server > Settings > Plugin > Available Plugins, and restart
2. In your store > Wallets > Lightning, Configure Breez
3. You will be on a page asking for:
   - Mnemonic: This is your 12 word seed phrase. YOU SHOULD GENERATE THIS SAFELY AND KEEP IT SAFE. If you have used Breez before, you can use the same seed phrase you used in Breez. This SEED PHRASE will be stored on BTCPAY SERVER.  IF YOU USE A SHARED BTCPAY SERVER, YOU ARE EXPOSING YOUR SEED PHRASE TO THE SERVER ADMINISTRATOR.
   - Greenlight credentials: In the case of a new seed, you'll need to acquire certificates for issuing new nodes from Blockstream. You can get these for free at https://greenlight.blockstream.com.
   - Invite Code: Alternatively, you may have an invite code which can be used instead of the Greenlight credentials.
4. Click Save
5. Your new lightning node will be created.
6. Your first lightning invoice will have a relatively high minimum amount limit. This is because Breez SDK requires a minimum amount to be able to open a channel.
7. You can now use your lightning node to receive payments.

NOTE: In the future, Blockstream Greenlight will offer a way to generate read-only access keys for your already issued node, so that you can use these instead of exposing your mnemonic phrase to BTCPay Server, allowing a lightweight, non-custodial lightning experience, even on shared BTCPay Server instances.

## Additional features

* Swap-in: Send and convert onchain funds to your Breez lightning nodes.
* Swap-out: Send and convert lightning funds to your onchain wallet.
