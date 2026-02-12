# BTCPay Server MCP Plugin

Connect LLM clients to your BTCPay Server instance using the [Model Context Protocol](https://modelcontextprotocol.io/) (MCP). Manage invoices, wallets, lightning channels, and more through natural language.

## Features

- **88 tools** covering the full BTCPay Server Greenfield API
- Streamable HTTP transport (stateless, no SSE fallback needed)
- Works with Claude Desktop, Claude Code, Claude Web/Mobile, Cursor, VS Code, Windsurf, and any MCP-compatible client
- API key authentication with `Authorization` header or `?token=` query parameter
- One-click API key generation from the setup UI

## Requirements

- BTCPay Server 2.3.0 or later

## Installation

1. In BTCPay Server, go to **Manage Plugins**
2. Find "MCP" in the plugin list
3. Click **Install**
4. Restart BTCPay Server when prompted

## Setup

1. Navigate to **MCP** in the top navigation bar
2. Click **Generate MCP API Key** (or create a custom key with specific permissions)
3. Copy the configuration snippet for your client
4. Paste it into your client's config file

### Claude Desktop / Claude Code

Add to `claude_desktop_config.json` or `.claude/mcp.json`:

```json
{
  "mcpServers": {
    "btcpayserver": {
      "type": "streamable-http",
      "url": "https://your-btcpay-server.com/plugins/mcp",
      "headers": {
        "Authorization": "token YOUR_API_KEY"
      }
    }
  }
}
```

### Claude Web / Mobile (Connectors)

1. Go to **Settings > Connectors** in Claude
2. Click **Add custom connector**
3. Enter a name (e.g. `BTCPay Server`)
4. Paste the URL: `https://your-btcpay-server.com/plugins/mcp?token=YOUR_API_KEY`

### Cursor

Add to `.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "btcpayserver": {
      "type": "streamable-http",
      "url": "https://your-btcpay-server.com/plugins/mcp",
      "headers": {
        "Authorization": "token YOUR_API_KEY"
      }
    }
  }
}
```

### VS Code

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "btcpayserver": {
      "type": "http",
      "url": "https://your-btcpay-server.com/plugins/mcp",
      "headers": {
        "Authorization": "token YOUR_API_KEY"
      }
    }
  }
}
```

### Other Clients

Any MCP-compatible client can connect using:

| Setting | Value |
|---------|-------|
| **Transport** | Streamable HTTP |
| **URL** | `https://your-btcpay-server.com/plugins/mcp` |
| **Auth Header** | `Authorization: token YOUR_API_KEY` |

## Available Tools

### Invoices (11 tools)
Create, list, update, archive, and refund invoices. View payment methods and mark invoice status.

### Lightning (18 tools)
Get node info and balance, manage channels, create and pay BOLT11 invoices, list payments, and configure lightning addresses.

### Wallets (12 tools)
Check balances, generate addresses, list transactions and UTXOs, create transactions, and view fee rate estimates.

### Apps (11 tools)
Create and manage Point of Sale and Crowdfund apps. View sales statistics and top-selling items.

### Stores (9 tools)
Create, update, and delete stores. Manage store users and roles.

### Payment Requests (7 tools)
Create shareable payment links, list and update requests, and create invoices from requests.

### Payouts & Pull Payments (10 tools)
Create pull payments, manage payouts, approve or cancel payouts, and mark payouts as paid.

### Payment Methods (4 tools)
List, configure, update, and remove payment methods (on-chain, Lightning, etc.).

### Rates (4 tools)
Get exchange rates, configure rate sources, and preview rate configurations.

### Webhooks (7 tools)
Create, update, and delete webhooks. View delivery history and retry failed deliveries.

### Notifications (4 tools)
List, view, update, and delete server notifications.

### Users (4 tools)
Get current user info, list users, and create new accounts (admin).

### Server (3 tools)
Check server health, get version/sync info, and list available rate sources.

## Authentication

The plugin uses BTCPay Server's Greenfield API authentication. API keys can have granular permissions — an unrestricted key gives the LLM full access, while a custom key can limit access to specific stores or operations.

The `?token=` query parameter is supported for clients that cannot send custom headers (e.g. Claude Connectors). The token is automatically promoted to an `Authorization` header by middleware.

## Example Prompts

Once connected, you can ask things like:

- "Show me today's invoices"
- "Create an invoice for 0.001 BTC"
- "What's my wallet balance?"
- "Open a lightning channel to this node"
- "List my stores and their payment methods"
- "Create a Point of Sale app with items for coffee and tea"
- "What are the current BTC/USD and BTC/EUR exchange rates?"
- "Send 100,000 sats to this lightning invoice"

## Support

For issues with this plugin, please open an issue on the [BTCPayServer Plugins repository](https://github.com/btcpayserver/btcpayserver-plugins).
