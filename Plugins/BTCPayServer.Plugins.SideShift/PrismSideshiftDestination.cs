namespace BTCPayServer.Plugins.SideShift;

public class PrismSideshiftDestination
{
    public string ShiftCoin { get; set; }
    public string ShiftNetwork { get; set; }
    public string ShiftDestination { get; set; }
    public string ShiftMemo { get; set; }
    public string SourceNetwork { get; set; }

    public bool Valid()
    {
        return !string.IsNullOrEmpty(ShiftCoin) && !string.IsNullOrEmpty(ShiftNetwork) &&
               !string.IsNullOrEmpty(ShiftDestination);
    }
}