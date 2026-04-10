using System;

namespace BTCPayServer.Plugins.Electrum;

public class ElectrumSettings
{
    public string Server { get; set; }
    public bool UseTls { get; set; } = true;
    public const int MaxGapLimit = 1000;
    private int _gapLimit = 20;
    public int GapLimit
    {
        get => _gapLimit;
        set => _gapLimit = Math.Clamp(value, 1, MaxGapLimit);
    }
    public string CryptoCode { get; set; } = "BTC";

    /// <summary>
    /// Trusted public Electrum servers sourced from Sparrow Wallet.
    /// All use SSL/TLS.
    /// </summary>
    public static readonly (string Host, int Port)[] TrustedServers =
    {
        ("blockstream.info", 700),
        ("electrum.blockstream.info", 50002),
        ("bitcoin.lu.ke", 50002),
        ("electrum.emzy.de", 50002),
        ("electrum.bitaroo.net", 50002),
        ("electrum.diynodes.com", 50022),
        ("fulcrum.sethforprivacy.com", 50002)
    };
}
