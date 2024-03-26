# Bringin Euro offramp plugin

Allows you to automatically forward received funds to Bringin, a Euro offramp provider.

## Usage

1. Install the plugin from the BTCPay Server > Settings > Plugin > Available Plugins, and restart
2. In the dashboard, or the side navigation, click on "Bringin" tostart configuring the plugin
3. You will need an API key, click on the onboarding link to start getting your account set up.
4. Once your account is set up, click on Integrations on the Bringin dashboard and get the API Key under BTCPay Server
5. Paste the API Key in the BTCPay Server plugin configuration, and new options should appear to configure the plugin
6. You can configure the available payment method options supported by Bringin, such as Lightning and On-chain.
7. Click Save
8. Make sure to configure payout processors so that payments to Bringin are automatically created.

## Flow
When an invoice on your store is paid and settled, every payment is counted per payment type (lightning, on-chain coming soon), relative to the "percentage" configured (set to 0 to not enable this payment).
Once the threshold is reached, an order is created on Bringin, and a payout paying this order is created. A payout processor then picks this payout and sends it to Bringin. Once the payment settles, the funds are automatically converted to Euro and the balance is reflected in your Bringin account and the BTCPay Server Bringin widget.

