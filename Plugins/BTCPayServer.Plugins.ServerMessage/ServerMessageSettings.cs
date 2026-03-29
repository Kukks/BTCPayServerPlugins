namespace BTCPayServer.Plugins.ServerMessage;

public class ServerMessageSettings
{
    public bool PublicEnabled { get; set; }
    public string PublicMessage { get; set; }
    public string PublicStyle { get; set; } = "warning";

    public bool AdminEnabled { get; set; }
    public string AdminMessage { get; set; }
    public string AdminStyle { get; set; } = "info";
}
