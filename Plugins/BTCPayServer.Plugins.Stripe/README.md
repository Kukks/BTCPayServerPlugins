# BTCPay Server Stripe Plugin

Accept fiat payments via Stripe alongside cryptocurrency payments in BTCPay Server.

## Features

- Accept credit/debit card payments through Stripe's Payment Element
- Apple Pay and Google Pay support
- Automatic webhook configuration for payment notifications
- Works alongside cryptocurrency payment methods
- Test mode support for development

## Requirements

- BTCPay Server 2.3.0 or later
- A Stripe account (https://stripe.com)

## Installation

1. In BTCPay Server, go to **Manage Plugins**
2. Find "Stripe" in the plugin list
3. Click **Install**
4. Restart BTCPay Server when prompted

## Configuration

### 1. Get Your Stripe API Keys

1. Log in to your [Stripe Dashboard](https://dashboard.stripe.com)
2. Go to **Developers > API keys**
3. Copy your **Publishable key** (starts with `pk_`)
4. Copy your **Secret key** (starts with `sk_`)

> **Tip:** Use test keys (`pk_test_` and `sk_test_`) for development and testing.

### 2. Configure the Plugin

1. In BTCPay Server, go to your store's **Settings**
2. Click **Stripe** in the left navigation
3. Enter your API keys:
   - **Publishable Key**: Your `pk_live_` or `pk_test_` key
   - **Secret Key**: Your `sk_live_` or `sk_test_` key
4. Set the **Settlement Currency** (e.g., USD, EUR, GBP)
5. Click **Save**

### 3. Test the Connection

Click **Test Connection** to verify your API keys are working correctly.

## Settings

| Setting | Description |
|---------|-------------|
| **Publishable Key** | Stripe publishable API key (required) |
| **Secret Key** | Stripe secret API key (required) |
| **Settlement Currency** | Currency for Stripe charges (default: USD) |
| **Statement Descriptor** | Text shown on customer bank statements (max 22 characters) |
| **Enable Apple Pay** | Allow Apple Pay payments (default: enabled) |
| **Enable Google Pay** | Allow Google Pay payments (default: enabled) |

## How It Works

1. When a customer chooses to pay with Stripe at checkout, a PaymentIntent is created
2. The customer enters their payment details in Stripe's secure Payment Element
3. On successful payment, BTCPay Server records the payment and updates the invoice
4. Webhooks provide backup notification in case of network issues

## Webhooks

The plugin automatically configures Stripe webhooks when you save your settings. Webhooks handle:

- `payment_intent.succeeded` - Records successful payments
- `payment_intent.payment_failed` - Logs failed payment attempts
- `charge.refunded` - Logs refund notifications
- `charge.dispute.created` - Logs dispute notifications

## Test Mode

When using test API keys, the plugin displays a warning banner. Test mode is useful for:

- Verifying your integration before going live
- Testing the checkout flow without real charges
- Using [Stripe test cards](https://stripe.com/docs/testing#cards)

## Troubleshooting

### Payments not being recorded

1. Check that your API keys are correct
2. Verify the webhook is configured (shown in settings)
3. Check BTCPay Server logs for errors

### "Invalid API Key" error

Ensure you're using matching key pairs:
- Both keys should be live (`pk_live_` + `sk_live_`) OR
- Both keys should be test (`pk_test_` + `sk_test_`)

### Apple Pay / Google Pay not showing

- Apple Pay requires HTTPS and domain verification in Stripe
- Google Pay requires domain registration in Stripe
- Both require the customer's device/browser to support them

## Support

For issues with this plugin, please open an issue on the [BTCPayServer Plugins repository](https://github.com/btcpayserver/btcpayserver-plugins).

For Stripe-specific questions, refer to [Stripe's documentation](https://stripe.com/docs).
