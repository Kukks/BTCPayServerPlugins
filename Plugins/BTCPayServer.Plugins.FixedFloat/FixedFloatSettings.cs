#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Plugins.FixedFloat
{
    public class FixedFloatSettings
    {
        public bool Enabled { get; set; }
        public decimal AmountMarkupPercentage { get; set; } = 0;

        public string? PreferredTargetPaymentMethodId { get; set; }

        public string[]? ExplicitMethods { get; set; }
        public bool OnlyShowExplicitMethods { get; set; } = false;

        public static Dictionary<string, string> AllowedSendingOptions = new()
        {
            {"AAVEETH", "Aave (ERC20)"},
            {"ADA", "Cardano"},
            {"ADABSC", "Cardano (BEP20)"},
            {"APT", "Aptos"},
            {"ATOM", "Cosmos"},
            {"AVAX", "Avalanche (C-Chain)"},
            {"BAT", "Basic Attention (ERC20)"},
            {"BSC", "BNB Smart Chain (BEP20)"},
            {"WBNBBSC", "Wrapped BNB (BEP20)"},
            {"BTC", "Bitcoin"},
            {"BTCBSC", "Bitcoin (BEP20)"},
            {"BTCLN", "Bitcoin (Lightning)"},
            {"BTT", "BitTorrent"},
            {"CAKE", "PancakeSwap (BEP20)"},
            {"DAIBSC", "DAI (BEP20)"},
            {"DAIETH", "DAI (ERC20)"},
            {"DAIMATIC", "DAI (Polygon)"},
            {"DASH", "Dash"},
            {"DOGE", "Dogecoin"},
            {"DOT", "Polkadot"},
            {"EOS", "EOS"},
            {"ETH", "Ethereum"},
            {"ETHARBITRUM", "Ethereum (Arbitrum)"},
            {"ETHBASE", "Ethereum (Base)"},
            {"ETHZKSYNC", "Ethereum (ZkSync)"},
            {"WETHARBITRUM", "Wrapped ETH (Arbitrum)"},
            {"WETHETH", "Wrapped ETH (ERC20)"},
            {"FTM", "Fantom"},
            {"KCS", "KuCoin Token"},
            {"LINK", "Chainlink (ERC20)"},
            {"LTC", "Litecoin"},
            {"MANAETH", "Decentraland (ERC20)"},
            {"MKR", "Maker (ERC20)"},
            {"PAXGETH", "PAX Gold (ERC20)"},
            {"POL", "Polygon"},
            {"POLETH", "Polygon (ERC20)"},
            {"SHIB", "SHIBA INU (ERC20)"},
            {"SHIBBSC", "SHIBA INU (BEP20)"},
            {"SOL", "Solana"},
            {"WSOL", "Wrapped SOL (Solana)"},
            {"TON", "Toncoin"},
            {"TRX", "Tron"},
            {"TUSD", "TrueUSD (ERC20)"},
            {"TWTBSC", "Trust Wallet Token (BEP20)"},
            {"USDCARBITRUM", "USD Coin (Arbitrum)"},
            {"USDCBSC", "USD Coin (BEP20)"},
            {"USDCETH", "USD Coin (ERC20)"},
            {"USDCMATIC", "USD Coin (Polygon)"},
            {"USDCSOL", "USD Coin (Solana)"},
            {"USDP", "Pax Dollar (ERC20)"},
            {"USDT", "Tether (ERC20)"},
            {"USDTBSC", "Tether (BEP20)"},
            {"USDTMATIC", "Tether (Polygon)"},
            {"USDTSOL", "Tether (Solana)"},
            {"USDTTRC", "Tether (TRC20)"},
            {"VET", "VeChain"},
            {"XLM", "Stellar Lumens"},
            {"XMR", "Monero"},
            {"XRP", "Ripple"},
            {"XTZ", "Tezos"},
            {"ZEC", "Zcash"},
            {"ZRX", "0x (ERC20)"}
        };


        public static List<SelectListItem> AllowedSendingOptionsList =>
            AllowedSendingOptions.Select(o => new SelectListItem(o.Value, o.Key)).ToList();
    }
}