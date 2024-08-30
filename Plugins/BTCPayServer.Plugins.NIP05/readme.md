# BTCPay Server NIP05 Support

This plugin allows your BTCPay Server to support 

* [Nostr](https://github.com/nostr-protocol/nostr)[ NIP05 protocol](https://github.com/nostr-protocol/nips/blob/master/05.md) to verify accounts.
* [Nostr](https://github.com/nostr-protocol/nostr)[ NIP57 protocol](https://github.com/nostr-protocol/nips/blob/master/57.md) to support Zaps.
* [Nostr](https://github.com/nostr-protocol/nostr)[ NIP47 protocol](https://github.com/nostr-protocol/nips/blob/master/47.md) to accept payments to your NWC enabled lightning wallet.

## Usage

* Install the plugin
* On a store you have owner access to, click on the new "Nostr" side navigation menu item
* Specify a name and public key.
NOTE: You will not be able to select the same name across different stores. The public key is in hex format and not `npub...` ([convert here](https://nostrcheck.me/converter/))
* Optionally include a set of relays that you primarily use so that client can discover your events more easily.

* Alternatively, you can import this data by using one of the Nostr browser extensions such as Alby or Nos2x 


Your NIP5 handle will be `name@yourbtcpayserver.domain`. If you have multiple domains mapped to the same btcpayserver, they will all be valid. 

