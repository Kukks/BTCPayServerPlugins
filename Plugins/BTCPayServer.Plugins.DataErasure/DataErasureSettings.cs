using System;

namespace BTCPayServer.Plugins.DataErasure
{
    public class DataErasureSettings
    {
        public bool Enabled { get; set; }
        public int DaysToKeep { get; set; }
        public DateTimeOffset? LastRunCutoff { get; set; }

        public bool EntirelyEraseInvoice { get; set; }
    }
}
