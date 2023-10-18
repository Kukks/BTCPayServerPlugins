# Sideshift for BTCPay Server plugin

This plugin integrates the no-kyc Sideshift exchanges into various parts of BTCPay Server.

* Invoice checkout - Let your customers pay with any coin supported by Sideshift. The settings allow you to show a payment method that loads sideshift with its various options as dropwdown, or you can explicitly show each option as a payment method on its own, or you can have both.
* Pull payments - Let your customers claim their payouts in any option supported by Sideshift.
* Prism Plugin - Allows you to use Sideshift as a destination in the Prism plugin, so that you can automatically convert incoming Bitcoin to any option Sideshift supports.

## Usage

For both invoices and pull payments, you will need to enable sideshift through its settings located in the plugins navigation under your store. The prism plugin integration does not require this to be on.

## Configuring on individual invoices

You can configure the sideshift options on individual invoices when creating them through the API by setting a json object under the Metadata property of the invoice. This will merge on top of your existing sideshift settings in your store. The json object should be in the following format, and any property not included, will use the ones on your store:

```json
{
  "sideshift": {
    "enabled": true, //whether it should be enabled/disabled for this invoice
    "explicitMethods": [ "USDT_liquid"], //if you want to explicitly show certain options, you can list them here. The format is currencyCode_network. You can look at the html in the sideshift settings page to see the full list of values.
    "onlyShowExplicitMethods" : true, // if you want to only show the explicit methods, and not the dropdown variant of the plugin
    "preferredTargetPaymentMethodId": "BTC_LightningLike", //if you want to set a preferred payment method that you would receive the funds from sideshift on. This is the payment method format as used in the BTCPay Server Greenfield API. 
    "amountMarkupPercentage": 0.5 //if you want to add a markup in case you dont think that sideshift is reliable in convertint into the exact amount.
  }
}
```