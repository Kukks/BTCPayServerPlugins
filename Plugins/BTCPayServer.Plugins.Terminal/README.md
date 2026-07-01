# Terminal

Turn any phone or tablet into a tap-to-pay register for your BTCPay Server store. Terminal lets a cashier's device and a customer's device share the *same* invoice through a single tap — no cashier login on the customer side, no manually copying payment links.

## What it does

- **Tap-to-pay registers.** Each till or counter becomes a *terminal* with two links you can write to NFC tags or share as QR codes.
- **Cashier check-in.** A cashier taps the *Check-in* tag once to bind their device to a terminal — no login needed on that device.
- **Automatic invoice routing.** Any invoice the cashier creates while checked in is automatically attached to that terminal.
- **Customer tap-to-pay.** The customer taps the *Customer* tag to open the current invoice on their own phone — or a fixed customer-facing screen shows it automatically.
- **Hot-swap.** Move a device to a different till by tapping a different check-in tag.

## How it works

A **terminal** represents one physical till / register / counter. Each terminal exposes two links:

| Link | Who uses it | What it does |
|------|-------------|--------------|
| **Check-in** (`/t/{id}/checkin`) | Cashier | Binds the tapping device (browser) to this terminal. Every invoice created from that device is then routed here. |
| **Customer** (`/t/{id}`) | Customer / customer screen | Opens the checkout for the terminal's current invoice. With no invoice yet, it shows a *waiting* screen that refreshes on its own and jumps to the checkout the moment a sale is rung up. |

A sale, start to finish:

1. The cashier's device is checked in to, say, *Register 1* (a one-time tap).
2. The cashier rings up a sale, creating an invoice from that device (Point of Sale app, keypad, API — however you normally do).
3. That invoice becomes *Register 1*'s current invoice.
4. The customer taps the *Register 1 — Customer* tag (or looks at the register's customer screen) and pays.

Check-in is remembered per browser and scoped to the store, so a device is bound to one terminal per store. Tapping a different check-in tag for that store re-binds it instantly.

## Setup

1. Install the **Terminal** plugin from *Manage Plugins* and restart when prompted.
2. In a store's left navigation, open **Terminal**. There is one Terminal per store — it holds all of that store's tills.
3. Add a terminal for each till and give it a clear name (e.g. *Counter 1*, *Register A*).
4. For each terminal, put its two links onto tags:
   - **Check-in** → the cashier's tag, kept behind the counter.
   - **Customer** → the customer-facing tag, or the URL a customer screen loads.

   You can write directly to an NFC tag from the terminal list (**Write NFC**, using Chrome on Android), or copy the link and encode it as a QR code.
5. Have the cashier tap **Check-in** on their device once per shift — then they're taking payments.

## Usage scenarios

- **Multi-till shop.** Create *Register 1…N*. Each cashier checks in their own device, and each register's customer tag/screen only ever shows that register's invoice.
- **Self-updating customer display.** Mount a cheap tablet at the till on the *Customer* URL. It idles on a waiting screen and automatically flips to the checkout when the cashier rings a sale — a hands-off customer-facing payment screen.
- **Market stall / pop-up.** One phone, several pitches: leave a check-in tag at each pitch and tap to move the phone between them as you roam.
- **Self-checkout.** Put the *Customer* tag on the counter so the shopper taps it to pull up and pay the current invoice without touching the cashier's device.

## Requirements & notes

- **Writing NFC tags** uses the [Web NFC API](https://developer.mozilla.org/docs/Web/API/Web_NFC_API), which today works in **Chrome on Android over HTTPS**. On other browsers, copy each link and encode it as a QR code, or program tags with any NFC-writer app.
- The **Customer** and **Check-in** pages need no BTCPay login — each link carries its own per-terminal id.
- Invoice routing is scoped to the store: a checked-in device only maps invoices created for its own store.
- Check-in is per store: one browser can act as a terminal in each of your stores independently. Each store has a single Terminal configuration.

## Support and community

BTCPay Server is built and maintained by contributors around the internet. Notice a bug, run into trouble, or want to request a feature? [Open an issue](https://github.com/Kukks/BTCPayServerPlugins/issues/new).

Come chat at [chat.btcpayserver.org](https://chat.btcpayserver.org/) or [t.me/btcpayserver](https://t.me/btcpayserver) if you need a hand.
