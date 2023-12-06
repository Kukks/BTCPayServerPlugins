# TicketTailor plugin for BTCPayServer

This plugin allows you to integrate [TicketTailor](https://www.tickettailor.com/) with BTCPay Server. 
It allows you to sell tickets for your events and accept payments in Bitcoin.

## Installation

1. Install the plugin from Plugins=>Add New=> TicketTailor
2. Restart BTCPay Server
3. Go to your Ticket Tailor account and add a [new API key](https://app.tickettailor.com/box-office/api#dpop=/box-office/api-key/add) with `Admin` role and "hide personal data from responses" unchecked. 
4. Go back to your BTCPay Server, choose the store to integrate with and click on Ticket Tailor in the navigation. This will create a ticket tailor app in your current store. 
5. Enter the API Key and save.
6. Now you should be able to select your Ticket tailor events in the dropdown. One selected, click save.
7. You should now have a "ticket purchase" button on your store's page. Clicking it will take you to the btcpayserver event purchase page.

## Flow
When a customer goes to the ticket purchase page, they can enter a name and must enter an email. Ticket Tailor requires a full name, so we generate one if not specified.
After the tickets are selected, the customer is redirected to the BTCPay Server checkout page, and a hold for the selected tickets is created to reserve the tickets for this customer. After the payment is sent, the customer is redirected to a custom receipt page where they can see their tickets. Tickets are only issued AFTER an invoice is settled. If an invoice is set to invalid or expired, the hold is deleted and the tickets are released for sale again.

## Additional Configuration

You should configure the [SMTP email settings in the store](https://docs.btcpayserver.org/Notifications/#store-emails) so that users receive the ticket link by email after an invoice is settled.
You're also able to override ticket names, prices and description on the BTCPay Server side.

## Secret Tickets

You can configure a ticket on ticket tailor to require an access code. BTCPay Server allows you to add `?accessCode=XXXX` to the ticket purchase page url to allow customers to view and purchase these secret tickets.
