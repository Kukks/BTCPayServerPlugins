using System;

namespace BTCPayServer.Plugins.Electrum.Data;

public class TrackedWallet
{
    public string Id { get; set; }
    public string CryptoCode { get; set; }
    public string DerivationStrategy { get; set; }
    public int ReceiveGapIndex { get; set; }
    public int ChangeGapIndex { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class TrackedAddress
{
    public string Scripthash { get; set; }
    public string WalletId { get; set; }
    public string KeyPath { get; set; }
    public byte[] ScriptPubKey { get; set; }
    public string Address { get; set; }
    public bool IsChange { get; set; }
    public bool IsUsed { get; set; }

    public TrackedWallet Wallet { get; set; }
}

public class TrackedUtxo
{
    public string Outpoint { get; set; }
    public string WalletId { get; set; }
    public string Scripthash { get; set; }
    public string Txid { get; set; }
    public int Vout { get; set; }
    public long Value { get; set; }
    public byte[] ScriptPubKey { get; set; }
    public string KeyPath { get; set; }
    public long? BlockHeight { get; set; }
    public string BlockHash { get; set; }
    public DateTimeOffset SeenAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsSpent { get; set; }
    public string SpendingTxid { get; set; }

    public TrackedWallet Wallet { get; set; }
    public TrackedAddress TrackedAddress { get; set; }
}

public class TrackedTransaction
{
    public string Txid { get; set; }
    public string WalletId { get; set; }
    public byte[] RawTx { get; set; }
    public long? BlockHeight { get; set; }
    public string BlockHash { get; set; }
    public long? Fee { get; set; }
    public DateTimeOffset SeenAt { get; set; } = DateTimeOffset.UtcNow;
    public long BalanceChange { get; set; }

    public TrackedWallet Wallet { get; set; }
}

public class SyncState
{
    public string Key { get; set; }
    public string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
