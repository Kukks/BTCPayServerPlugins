# Subscriptions plugin for BTCPay Server

This plugin allows you to create subscriptions in BTCPay Server.

Essentially you create an subscriptions app from the side navigation on your store, you configure a price, duration and description, and it will create a public page where your customers can subscribe to your service.

When they click on the subscribe button, a payment request is created. If Settled, a new subscription is created, and a button on the payment request page will appear leading them to their subscription page.

You can configure the subscription to set a form to be filled by the customer on the first payment request created. It is recommended to set this and collect an email under the `buyerEmail` field, as this 
plugin provides new webhooks for subscription status changes and subscription renewal notices. 

Additionally there is one new Greenfield API endpoint: GET https://btcpay.host/api/v1/apps/subscriptions/appId 

If the subscription is about to expire (within 3 days), a payment request is created to extend the period of the subscription. This payment request expires when the subscription expires. if this happens, the subscription is marked as inactive

Quick demo:
https://streamable.com/y9gimo

## Email rules:

There are 2 new email rules:

* A subscription status has been updated: has placeholders:{Subscription.SubscriptionId},{Subscription.Status},{Subscription.AppId}
* "A subscription has generated a payment request for renewal: has placeholders: {Subscription.SubscriptionId}, {Subscription.PaymentRequestId},{Subscription.AppId}
