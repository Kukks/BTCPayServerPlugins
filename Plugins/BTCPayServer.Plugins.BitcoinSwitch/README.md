# Bitcoin Switch Plugin

This plugin allows you to connect your BTCPay Server to the Bitcoin Switch hardware, developed by the amazing LNURL team.

## Installation

1. Go to the "Plugins" page of your BTCPay Server
2. Click on install under the "Bitcoin Switch" plugin listing
3. Restart BTCPay Server
4. Go to your point of sale or crowdfund app settings
5. Click on "Edit" on the item/perk you'd like to enable Bitcoin Switch for.
6. Specify the hardware GPIO pin and duration of activation 
7. Close the editor
8. Choose Print Display in Point of Sale style (if you want to be able to print LNURL QR codes for each item specifically).
9. Save the app
10. Your websocket url is the point of sale url, appended with "/bitcoinswitch" and the scheme set to wss:// (e.g. wss://mybtcpay.com/apps/A9xD2nxuWzQTh33E9U6YvyyXrvA/pos/bitcoinswitch)
11. Upon purchase (invoice marked as settled), any open websockets will receive the message to activate (io-duration)
12. Configure your hardware using https://bitcoinswitch.lnbits.com/
