using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Terminal;

public class TerminalSettings
{
    public List<TerminalData> Terminals { get; set; } = new();
}

public class TerminalData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; }
}
