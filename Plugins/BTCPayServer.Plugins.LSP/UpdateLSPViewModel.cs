using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Plugins.LSP;

public class LSPSettings
{
    public bool Enabled { get; set; } = true;
    public long Minimum { get; set; } = 100000;
    public long Maximum { get; set; } = 10000000;
    public decimal FeePerSat { get; set; } = 0.01m;
    public long BaseFee { get; set; } = 0;
    public string CustomCSS { get; set; }
    public string Title { get; set; } = "Lightning Liquidity Peddler";
    public string Description { get; set; } = "<h3 class='w-100'>Get an inbound channel</h3><p>This will open a public channel to your node.</p>";
}

public class LSPViewModel
{
    public LSPSettings Settings { get; set; }
}
