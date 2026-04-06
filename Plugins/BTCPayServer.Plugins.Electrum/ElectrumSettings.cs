namespace BTCPayServer.Plugins.Electrum;

public class ElectrumSettings
{
    public string Server { get; set; }
    public bool UseTls { get; set; } = true;
    public int GapLimit { get; set; } = 20;
    public string CryptoCode { get; set; } = "BTC";
}
