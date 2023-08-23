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
            {"ATOM", "Cosmos"},
            {"AVAX", "Avalanche (C-Chain)"},
            {"BAT", "Basic Attention (ERC20)"},
            {"BCH", "Bitcoin Cash"},
            {"BNB", "BNB Beacon Chain (BEP2)"},
            {"BSC", "BNB Smart Chain (BEP20)"},
            {"BTC", "Bitcoin"},
            {"BTCLN", "Bitcoin (Lightning)"},
            {"BTT", "BitTorrent"},
            {"BUSD", "Binance USD (BEP2)"},
            {"BUSDBSC", "Binance USD (BEP20)"},
            {"BUSDETH", "Binance USD (ERC20)"},
            {"CAKE", "PancakeSwap (BEP20)"},
            {"DAIETH", "DAI (ERC20)"},
            {"DOGE", "Dogecoin"},
            {"DOT", "Polkadot"},
            {"EOS", "EOS"},
            {"ETC", "Ethereum Classic"},
            {"ETH", "Ethereum"},
            {"FTM", "Fantom"},
            {"LINK", "Chainlink (ERC20)"},
            {"LTC", "Litecoin"},
            {"MANAETH", "Decentraland (ERC20)"},
            {"MATIC", "Polygon"},
            {"MATICETH", "Polygon (ERC20)"},
            {"MKR", "Maker (ERC20)"},
            {"SHIB", "SHIBA INU (ERC20)"},
            {"SOL", "Solana"},
            {"TON", "Toncoin"},
            {"TRX", "Tron"},
            {"TUSD", "TrueUSD (ERC20)"},
            {"TWT", "Trust Wallet Token (BEP2)"},
            {"TWTBSC", "Trust Wallet Token (BEP20)"},
            {"USDCETH", "USD Coin (ERC20)"},
            {"USDCSOL", "USD Coin (Solana)"},
            {"USDCTRC", "USD Coin (TRC20)"},
            {"USDP", "Pax Dollar (ERC20)"},
            {"USDT", "Tether (ERC20)"},
            {"USDTSOL", "Tether (Solana)"},
            {"USDTTRC", "Tether (TRC20)"},
            {"VET", "VeChain"},
            {"XMR", "Monero"},
            {"XRP", "Ripple"},
            {"XTZ", "Tezos"},
            {"ZEC", "Zcash"},
            {"ZRX", "0x (ERC20)"}
        };
        
        public static List<SelectListItem> AllowedSendingOptionsList => AllowedSendingOptions.Select(o => new SelectListItem(o.Value, o.Key)).ToList();


    }
}
