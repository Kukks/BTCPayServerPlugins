namespace BTCPayServer.Plugins.Prism;

public record PendingPayout(long PayoutAmount, long FeeCharged, string DestinationId);