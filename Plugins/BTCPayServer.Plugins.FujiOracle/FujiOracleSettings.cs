using System.Collections.Generic;

namespace BTCPayServer.Plugins.FujiOracle
{
    public class FujiOracleSettings
    {
        public bool Enabled { get; set; }
        public string Key { get; set; }
        public List<string> Pairs { get; set; } = new();
    }
}
