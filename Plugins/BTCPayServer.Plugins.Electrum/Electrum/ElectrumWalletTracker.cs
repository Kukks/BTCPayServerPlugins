using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Electrum.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Electrum;

public class ElectrumWalletTracker
{
    private readonly ElectrumClient _client;
    private readonly ElectrumDbContextFactory _dbFactory;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ElectrumSettings _settings;
    private readonly ILogger<ElectrumWalletTracker> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private readonly ConcurrentDictionary<string, DerivationStrategyBase> _trackedStrategies = new();
    private Network _network;
    private DerivationStrategyFactory _derivationFactory;
    private int _tipHeight;

    public ElectrumWalletTracker(
        ElectrumClient client,
        ElectrumDbContextFactory dbFactory,
        BTCPayNetworkProvider networkProvider,
        IOptions<ElectrumSettings> settings,
        ILogger<ElectrumWalletTracker> logger)
    {
        _client = client;
        _dbFactory = dbFactory;
        _networkProvider = networkProvider;
        _settings = settings.Value;
        _logger = logger;

        var btcNetwork = _networkProvider.GetNetwork<BTCPayNetwork>(_settings.CryptoCode ?? "BTC");
        if (btcNetwork != null)
        {
            _network = btcNetwork.NBitcoinNetwork;
            _derivationFactory = new DerivationStrategyFactory(_network);
        }
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // Ensure schema exists
            await using var ctx = _dbFactory.CreateContext();
            await ctx.Database.MigrateAsync(ct);

            // Load all tracked wallets and re-subscribe
            var wallets = await ctx.TrackedWallets.ToListAsync(ct);
            foreach (var wallet in wallets)
            {
                var strategy = ParseStrategy(wallet.DerivationStrategy);
                if (strategy == null) continue;

                _trackedStrategies[wallet.Id] = strategy;

                var addresses = await ctx.TrackedAddresses
                    .Where(a => a.WalletId == wallet.Id)
                    .ToListAsync(ct);

                foreach (var addr in addresses)
                {
                    await _client.ScripthashSubscribeAsync(addr.Scripthash, ct);
                }

                // Diff state for each address
                await SyncWalletStateAsync(wallet.Id, addresses, ct);
            }

            _logger.LogInformation("Initialized {Count} tracked wallets", wallets.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task TrackWalletAsync(string strategyStr, CancellationToken ct)
    {
        var strategy = ParseStrategy(strategyStr);
        if (strategy == null)
            throw new InvalidOperationException($"Cannot parse derivation strategy: {strategyStr}");

        await _lock.WaitAsync(ct);
        try
        {
            await using var ctx = _dbFactory.CreateContext();

            var existing = await ctx.TrackedWallets.FindAsync(new object[] { strategyStr }, ct);
            if (existing != null)
            {
                _trackedStrategies[strategyStr] = strategy;
                return;
            }

            var wallet = new TrackedWallet
            {
                Id = strategyStr,
                CryptoCode = _settings.CryptoCode ?? "BTC",
                DerivationStrategy = strategyStr,
                ReceiveGapIndex = _settings.GapLimit - 1,
                ChangeGapIndex = _settings.GapLimit - 1
            };

            ctx.TrackedWallets.Add(wallet);

            // Derive initial addresses
            var addresses = DeriveAddresses(strategy, false, 0, _settings.GapLimit);
            addresses.AddRange(DeriveAddresses(strategy, true, 0, _settings.GapLimit));

            foreach (var addr in addresses)
            {
                addr.WalletId = strategyStr;
                ctx.TrackedAddresses.Add(addr);
            }

            await ctx.SaveChangesAsync(ct);
            _trackedStrategies[strategyStr] = strategy;

            // Subscribe all addresses
            foreach (var addr in addresses)
            {
                await _client.ScripthashSubscribeAsync(addr.Scripthash, ct);
            }

            // Fetch initial state
            await SyncWalletStateAsync(strategyStr, addresses, ct);

            _logger.LogInformation("Now tracking wallet {Strategy}", strategyStr);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<NewTransactionInfo>> HandleScripthashNotificationAsync(
        string scripthash, string status, CancellationToken ct)
    {
        var newTxs = new List<NewTransactionInfo>();

        await _lock.WaitAsync(ct);
        try
        {
            await using var ctx = _dbFactory.CreateContext();

            var addr = await ctx.TrackedAddresses.FindAsync(new object[] { scripthash }, ct);
            if (addr == null) return newTxs;

            // Fetch current history from Electrum
            var history = await _client.ScripthashGetHistoryAsync(scripthash, ct);
            var existingTxids = await ctx.Transactions
                .Where(t => t.WalletId == addr.WalletId)
                .Select(t => t.Txid)
                .ToHashSetAsync(ct);

            if (!_trackedStrategies.TryGetValue(addr.WalletId, out var strategy))
                return newTxs;

            foreach (var item in history)
            {
                if (existingTxids.Contains(item.TxHash))
                {
                    // Update confirmation if needed
                    var existing = await ctx.Transactions
                        .FirstOrDefaultAsync(t => t.Txid == item.TxHash && t.WalletId == addr.WalletId, ct);
                    if (existing != null && existing.BlockHeight != item.Height && item.Height > 0)
                    {
                        existing.BlockHeight = item.Height;
                    }
                    continue;
                }

                // New transaction
                var rawHex = await _client.TransactionGetAsync(item.TxHash, ct);
                var tx = Transaction.Parse(rawHex, _network);
                var balanceChange = ComputeBalanceChange(ctx, tx, addr.WalletId);

                var trackedTx = new TrackedTransaction
                {
                    Txid = item.TxHash,
                    WalletId = addr.WalletId,
                    RawTx = tx.ToBytes(),
                    BlockHeight = item.Height > 0 ? item.Height : null,
                    Fee = item.Fee > 0 ? item.Fee : null,
                    BalanceChange = balanceChange,
                    SeenAt = DateTimeOffset.UtcNow
                };

                ctx.Transactions.Add(trackedTx);

                var txInfo = BuildNewTransactionInfo(tx, addr, strategy, item);
                if (txInfo != null)
                    newTxs.Add(txInfo);
            }

            // Update UTXOs
            var utxos = await _client.ScripthashListUnspentAsync(scripthash, ct);
            await UpdateUtxosForAddress(ctx, addr, utxos, ct);

            // Mark address as used
            if (history.Length > 0 && !addr.IsUsed)
            {
                addr.IsUsed = true;
                await ExtendGapIfNeeded(ctx, addr, ct);
            }

            await ctx.SaveChangesAsync(ct);
        }
        finally
        {
            _lock.Release();
        }

        return newTxs;
    }

    public async Task HandleNewBlockAsync(int height, CancellationToken ct)
    {
        _tipHeight = height;

        await _lock.WaitAsync(ct);
        try
        {
            await using var ctx = _dbFactory.CreateContext();

            // Update sync state
            var state = await ctx.SyncStates.FindAsync(new object[] { "tip_height" }, ct);
            if (state != null)
            {
                state.Value = height.ToString();
                state.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                ctx.SyncStates.Add(new SyncState
                {
                    Key = "tip_height",
                    Value = height.ToString(),
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            // Re-check unconfirmed transactions
            var unconfirmed = await ctx.Transactions
                .Where(t => t.BlockHeight == null || t.BlockHeight == 0)
                .ToListAsync(ct);

            foreach (var tx in unconfirmed)
            {
                var addresses = await ctx.TrackedAddresses
                    .Where(a => a.WalletId == tx.WalletId)
                    .ToListAsync(ct);

                foreach (var addr in addresses)
                {
                    var history = await _client.ScripthashGetHistoryAsync(addr.Scripthash, ct);
                    var match = history.FirstOrDefault(h => h.TxHash == tx.Txid);
                    if (match != null && match.Height > 0)
                    {
                        tx.BlockHeight = match.Height;
                        break;
                    }
                }
            }

            // Update UTXO confirmations
            var unconfirmedUtxos = await ctx.Utxos
                .Where(u => u.BlockHeight == null || u.BlockHeight == 0)
                .ToListAsync(ct);

            foreach (var utxo in unconfirmedUtxos)
            {
                var addr = await ctx.TrackedAddresses.FindAsync(new object[] { utxo.Scripthash }, ct);
                if (addr == null) continue;

                var utxoList = await _client.ScripthashListUnspentAsync(addr.Scripthash, ct);
                var match = utxoList.FirstOrDefault(u =>
                    u.TxHash == utxo.Txid && u.TxPos == utxo.Vout);

                if (match != null && match.Height > 0)
                {
                    utxo.BlockHeight = match.Height;
                }
            }

            await ctx.SaveChangesAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ─────────────────────────────────────────────
    // Methods called by ElectrumHttpHandler
    // ─────────────────────────────────────────────

    public async Task<KeyPathInformation> GetNextUnusedAddressAsync(
        string strategyStr, bool isChange, bool reserve, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();

        var addr = await ctx.TrackedAddresses
            .Where(a => a.WalletId == strategyStr && a.IsChange == isChange && !a.IsUsed)
            .OrderBy(a => a.KeyPath)
            .FirstOrDefaultAsync(ct);

        if (addr == null)
        {
            // Might need to track first
            await TrackWalletAsync(strategyStr, ct);
            addr = await ctx.TrackedAddresses
                .Where(a => a.WalletId == strategyStr && a.IsChange == isChange && !a.IsUsed)
                .OrderBy(a => a.KeyPath)
                .FirstOrDefaultAsync(ct);
        }

        if (addr == null) return null;

        if (reserve)
        {
            addr.IsUsed = true;
            await ctx.SaveChangesAsync(ct);
        }

        var script = Script.FromBytesUnsafe(addr.ScriptPubKey);
        var address = script.GetDestinationAddress(_network);

        return new KeyPathInformation
        {
            Address = address,
            ScriptPubKey = script,
            KeyPath = KeyPath.Parse(addr.KeyPath),
            Feature = isChange ? DerivationFeature.Change : DerivationFeature.Deposit,
            TrackedSource = TrackedSource.Create(ParseStrategy(strategyStr))
        };
    }

    public async Task<UTXOChanges> GetUTXOChangesAsync(string strategyStr, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();

        var utxos = await ctx.Utxos
            .Where(u => u.WalletId == strategyStr && !u.IsSpent)
            .ToListAsync(ct);

        var confirmed = new List<UTXO>();
        var unconfirmed = new List<UTXO>();

        foreach (var u in utxos)
        {
            var utxo = new UTXO
            {
                Outpoint = OutPoint.Parse(u.Outpoint),
                Value = Money.Satoshis(u.Value),
                ScriptPubKey = Script.FromBytesUnsafe(u.ScriptPubKey),
                KeyPath = KeyPath.Parse(u.KeyPath),
                Timestamp = u.SeenAt,
                Confirmations = u.BlockHeight.HasValue && _tipHeight > 0
                    ? _tipHeight - (int)u.BlockHeight.Value + 1
                    : 0
            };

            if (u.BlockHeight.HasValue && u.BlockHeight > 0)
                confirmed.Add(utxo);
            else
                unconfirmed.Add(utxo);
        }

        var result = new UTXOChanges
        {
            CurrentHeight = _tipHeight,
            Confirmed = new UTXOChange { UTXOs = confirmed },
            Unconfirmed = new UTXOChange { UTXOs = unconfirmed }
        };

        return result;
    }

    public async Task<GetBalanceResponse> GetBalanceAsync(string strategyStr, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();

        var utxos = await ctx.Utxos
            .Where(u => u.WalletId == strategyStr && !u.IsSpent)
            .ToListAsync(ct);

        var confirmed = utxos.Where(u => u.BlockHeight.HasValue && u.BlockHeight > 0).Sum(u => u.Value);
        var unconfirmed = utxos.Where(u => !u.BlockHeight.HasValue || u.BlockHeight <= 0).Sum(u => u.Value);

        return new GetBalanceResponse
        {
            Confirmed = Money.Satoshis(confirmed),
            Unconfirmed = Money.Satoshis(unconfirmed),
            Available = Money.Satoshis(confirmed + unconfirmed),
            Total = Money.Satoshis(confirmed + unconfirmed)
        };
    }

    public async Task<TransactionResult> GetTransactionResultAsync(string txId, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();

        var tx = await ctx.Transactions
            .FirstOrDefaultAsync(t => t.Txid == txId, ct);

        if (tx == null)
        {
            // Try fetching from Electrum directly
            try
            {
                var rawHex = await _client.TransactionGetAsync(txId, ct);
                var parsed = Transaction.Parse(rawHex, _network);
                return new TransactionResult
                {
                    TransactionHash = parsed.GetHash(),
                    Transaction = parsed,
                    Confirmations = 0,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }
            catch
            {
                return null;
            }
        }

        var transaction = tx.RawTx != null ? Transaction.Load(tx.RawTx, _network) : null;
        var confirmations = tx.BlockHeight.HasValue && tx.BlockHeight > 0 && _tipHeight > 0
            ? _tipHeight - (int)tx.BlockHeight.Value + 1
            : 0;

        return new TransactionResult
        {
            TransactionHash = uint256.Parse(tx.Txid),
            Transaction = transaction,
            Confirmations = confirmations,
            Height = tx.BlockHeight.HasValue ? (int)tx.BlockHeight.Value : 0,
            Timestamp = tx.SeenAt
        };
    }

    public async Task<TransactionInformation> GetTransactionInfoAsync(
        string strategyStr, string txId, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();

        var tx = await ctx.Transactions
            .FirstOrDefaultAsync(t => t.Txid == txId && t.WalletId == strategyStr, ct);

        if (tx == null) return null;

        var transaction = tx.RawTx != null ? Transaction.Load(tx.RawTx, _network) : null;
        var confirmations = tx.BlockHeight.HasValue && tx.BlockHeight > 0 && _tipHeight > 0
            ? _tipHeight - (int)tx.BlockHeight.Value + 1
            : 0;

        return new TransactionInformation
        {
            TransactionId = uint256.Parse(tx.Txid),
            Transaction = transaction,
            Confirmations = confirmations,
            Height = tx.BlockHeight.HasValue ? (int)tx.BlockHeight.Value : 0,
            Timestamp = tx.SeenAt,
            BalanceChange = Money.Satoshis(tx.BalanceChange)
        };
    }

    public async Task<GetTransactionsResponse> GetTransactionsResponseAsync(
        string strategyStr, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();

        var txs = await ctx.Transactions
            .Where(t => t.WalletId == strategyStr)
            .OrderByDescending(t => t.SeenAt)
            .ToListAsync(ct);

        var confirmed = new List<TransactionInformation>();
        var unconfirmed = new List<TransactionInformation>();

        foreach (var tx in txs)
        {
            var transaction = tx.RawTx != null ? Transaction.Load(tx.RawTx, _network) : null;
            var confirmations = tx.BlockHeight.HasValue && tx.BlockHeight > 0 && _tipHeight > 0
                ? _tipHeight - (int)tx.BlockHeight.Value + 1
                : 0;

            var info = new TransactionInformation
            {
                TransactionId = uint256.Parse(tx.Txid),
                Transaction = transaction,
                Confirmations = confirmations,
                Height = tx.BlockHeight.HasValue ? (int)tx.BlockHeight.Value : 0,
                Timestamp = tx.SeenAt,
                BalanceChange = Money.Satoshis(tx.BalanceChange)
            };

            if (tx.BlockHeight.HasValue && tx.BlockHeight > 0)
                confirmed.Add(info);
            else
                unconfirmed.Add(info);
        }

        return new GetTransactionsResponse
        {
            Height = _tipHeight,
            ConfirmedTransactions = new TransactionInformationSet { Transactions = confirmed },
            UnconfirmedTransactions = new TransactionInformationSet { Transactions = unconfirmed },
            ReplacedTransactions = new TransactionInformationSet { Transactions = new List<TransactionInformation>() }
        };
    }

    public async Task<BroadcastResult> BroadcastAsync(string body, CancellationToken ct)
    {
        try
        {
            // body may be JSON with raw tx, or just raw tx hex
            string rawTx;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                rawTx = doc.RootElement.GetProperty("transaction").GetString();
            }
            catch
            {
                rawTx = body.Trim().Trim('"');
            }

            var txId = await _client.TransactionBroadcastAsync(rawTx, ct);
            return new BroadcastResult(true);
        }
        catch (ElectrumException ex)
        {
            return new BroadcastResult(false)
            {
                RPCMessage = ex.Message
            };
        }
    }

    public async Task<GetFeeRateResult> GetFeeRateAsync(int blockTarget, CancellationToken ct)
    {
        var btcPerKb = await _client.EstimateFeeAsync(blockTarget, ct);
        FeeRate rate;
        if (btcPerKb <= 0)
        {
            rate = new FeeRate(1.0m);
        }
        else
        {
            var satPerByte = btcPerKb * 100_000m;
            rate = new FeeRate(satPerByte);
        }

        return new GetFeeRateResult
        {
            FeeRate = rate,
            BlockCount = blockTarget
        };
    }

    public StatusResult GetStatus()
    {
        return new StatusResult
        {
            IsFullySynched = _client.IsConnected,
            ChainHeight = _tipHeight,
            SyncHeight = _tipHeight,
            Version = "electrum-plugin-1.0.0",
            SupportedCryptoCodes = new[] { _settings.CryptoCode ?? "BTC" },
            NetworkType = _network?.ChainName ?? ChainName.Mainnet
        };
    }

    // ─────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────

    private DerivationStrategyBase ParseStrategy(string strategyStr)
    {
        if (_derivationFactory == null) return null;
        try
        {
            return _derivationFactory.Parse(strategyStr);
        }
        catch
        {
            _logger.LogWarning("Failed to parse derivation strategy: {Strategy}", strategyStr);
            return null;
        }
    }

    private List<TrackedAddress> DeriveAddresses(
        DerivationStrategyBase strategy, bool isChange, int fromIndex, int count)
    {
        var result = new List<TrackedAddress>();
        var feature = isChange ? DerivationFeature.Change : DerivationFeature.Deposit;
        var line = strategy.GetLineFor(feature);

        for (var i = fromIndex; i < fromIndex + count; i++)
        {
            var derivation = line.Derive((uint)i);
            var script = derivation.ScriptPubKey;
            var scripthash = ScriptHashUtility.ComputeScriptHash(script);
            var address = script.GetDestinationAddress(_network);

            var chainIndex = isChange ? 1 : 0;
            result.Add(new TrackedAddress
            {
                Scripthash = scripthash,
                KeyPath = $"{chainIndex}/{i}",
                ScriptPubKey = script.ToBytes(),
                Address = address?.ToString() ?? "",
                IsChange = isChange,
                IsUsed = false
            });
        }

        return result;
    }

    private async Task SyncWalletStateAsync(
        string walletId, List<TrackedAddress> addresses, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();

        foreach (var addr in addresses)
        {
            try
            {
                // Get current history
                var history = await _client.ScripthashGetHistoryAsync(addr.Scripthash, ct);
                var existingTxids = await ctx.Transactions
                    .Where(t => t.WalletId == walletId)
                    .Select(t => t.Txid)
                    .ToHashSetAsync(ct);

                foreach (var item in history)
                {
                    if (existingTxids.Contains(item.TxHash))
                    {
                        // Update height if confirmed
                        var existing = await ctx.Transactions
                            .FirstOrDefaultAsync(t => t.Txid == item.TxHash && t.WalletId == walletId, ct);
                        if (existing != null && item.Height > 0 && existing.BlockHeight != item.Height)
                        {
                            existing.BlockHeight = item.Height;
                        }
                        continue;
                    }

                    var rawHex = await _client.TransactionGetAsync(item.TxHash, ct);
                    var tx = Transaction.Parse(rawHex, _network);
                    var balanceChange = ComputeBalanceChange(ctx, tx, walletId);

                    ctx.Transactions.Add(new TrackedTransaction
                    {
                        Txid = item.TxHash,
                        WalletId = walletId,
                        RawTx = tx.ToBytes(),
                        BlockHeight = item.Height > 0 ? item.Height : null,
                        Fee = item.Fee > 0 ? item.Fee : null,
                        BalanceChange = balanceChange
                    });

                    if (!addr.IsUsed)
                    {
                        addr.IsUsed = true;
                        var tracked = await ctx.TrackedAddresses.FindAsync(new object[] { addr.Scripthash }, ct);
                        if (tracked != null)
                            tracked.IsUsed = true;
                    }
                }

                // Sync UTXOs
                var utxos = await _client.ScripthashListUnspentAsync(addr.Scripthash, ct);
                await UpdateUtxosForAddress(ctx, addr, utxos, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error syncing address {Address} ({Scripthash})",
                    addr.Address, addr.Scripthash);
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    private async Task UpdateUtxosForAddress(
        ElectrumDbContext ctx, TrackedAddress addr,
        ElectrumUnspentItem[] utxos, CancellationToken ct)
    {
        var currentOutpoints = utxos.Select(u => $"{u.TxHash}:{u.TxPos}").ToHashSet();

        // Mark spent UTXOs
        var existingUtxos = await ctx.Utxos
            .Where(u => u.Scripthash == addr.Scripthash && !u.IsSpent)
            .ToListAsync(ct);

        foreach (var existing in existingUtxos)
        {
            if (!currentOutpoints.Contains(existing.Outpoint))
            {
                existing.IsSpent = true;
            }
        }

        // Add new UTXOs
        var existingOutpoints = existingUtxos.Select(u => u.Outpoint).ToHashSet();
        foreach (var utxo in utxos)
        {
            var outpoint = $"{utxo.TxHash}:{utxo.TxPos}";
            if (existingOutpoints.Contains(outpoint)) continue;

            ctx.Utxos.Add(new TrackedUtxo
            {
                Outpoint = outpoint,
                WalletId = addr.WalletId,
                Scripthash = addr.Scripthash,
                Txid = utxo.TxHash,
                Vout = utxo.TxPos,
                Value = utxo.Value,
                ScriptPubKey = addr.ScriptPubKey,
                KeyPath = addr.KeyPath,
                BlockHeight = utxo.Height > 0 ? utxo.Height : null,
                SeenAt = DateTimeOffset.UtcNow
            });
        }
    }

    private long ComputeBalanceChange(ElectrumDbContext ctx, Transaction tx, string walletId)
    {
        // Look up which of our addresses are involved
        var ourScripts = ctx.TrackedAddresses
            .Where(a => a.WalletId == walletId)
            .Select(a => a.ScriptPubKey)
            .ToHashSet(new ByteArrayComparer());

        long change = 0;

        // Outputs to us are positive
        foreach (var output in tx.Outputs)
        {
            if (ourScripts.Contains(output.ScriptPubKey.ToBytes()))
            {
                change += output.Value.Satoshi;
            }
        }

        // Inputs from us are negative (we'd need to look up the previous output)
        // For simplicity, we check if the spent outpoint matches our UTXOs
        foreach (var input in tx.Inputs)
        {
            var prevOutpoint = $"{input.PrevOut.Hash}:{input.PrevOut.N}";
            var ourUtxo = ctx.Utxos.Local.FirstOrDefault(u => u.Outpoint == prevOutpoint && u.WalletId == walletId)
                          ?? ctx.Utxos.FirstOrDefault(u => u.Outpoint == prevOutpoint && u.WalletId == walletId);
            if (ourUtxo != null)
            {
                change -= ourUtxo.Value;
            }
        }

        return change;
    }

    private async Task ExtendGapIfNeeded(ElectrumDbContext ctx, TrackedAddress usedAddr, CancellationToken ct)
    {
        var parts = usedAddr.KeyPath.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var index))
            return;

        var wallet = await ctx.TrackedWallets.FindAsync(new object[] { usedAddr.WalletId }, ct);
        if (wallet == null) return;

        if (!_trackedStrategies.TryGetValue(wallet.Id, out var strategy))
            return;

        var currentGapIndex = usedAddr.IsChange ? wallet.ChangeGapIndex : wallet.ReceiveGapIndex;
        var gapLimit = _settings.GapLimit;

        // If the used address is within gapLimit of the current boundary, extend
        if (index >= currentGapIndex - gapLimit + 1)
        {
            var newGapIndex = index + gapLimit;
            var deriveFrom = currentGapIndex + 1;
            var deriveCount = newGapIndex - currentGapIndex;

            if (deriveCount > 0)
            {
                var newAddresses = DeriveAddresses(strategy, usedAddr.IsChange, deriveFrom, deriveCount);
                foreach (var addr in newAddresses)
                {
                    addr.WalletId = wallet.Id;
                    ctx.TrackedAddresses.Add(addr);
                    await _client.ScripthashSubscribeAsync(addr.Scripthash, ct);
                }

                if (usedAddr.IsChange)
                    wallet.ChangeGapIndex = newGapIndex;
                else
                    wallet.ReceiveGapIndex = newGapIndex;
            }
        }
    }

    private class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[] x, byte[] y) => x.SequenceEqual(y);
        public int GetHashCode(byte[] obj) => BitConverter.ToInt32(obj, 0);
    }

    // ─────────────────────────────────────────────
    // NewTransactionInfo for the listener
    // ─────────────────────────────────────────────

    public class NewTransactionInfo
    {
        public string TxId { get; set; }
        public DerivationStrategyBase DerivationStrategy { get; set; }
        public int Confirmations { get; set; }
        public bool IsRbf { get; set; }
        public DateTimeOffset SeenAt { get; set; }
        public List<OutputInfo> Outputs { get; set; } = new();
    }

    public class OutputInfo
    {
        public string Address { get; set; }
        public int Index { get; set; }
        public Money Value { get; set; }
        public string KeyPath { get; set; }
        public int KeyIndex { get; set; }
    }

    private NewTransactionInfo BuildNewTransactionInfo(
        Transaction tx, TrackedAddress matchedAddr,
        DerivationStrategyBase strategy, ElectrumHistoryItem historyItem)
    {
        var info = new NewTransactionInfo
        {
            TxId = tx.GetHash().ToString(),
            DerivationStrategy = strategy,
            Confirmations = historyItem.Height > 0 && _tipHeight > 0
                ? _tipHeight - historyItem.Height + 1 : 0,
            IsRbf = tx.RBF,
            SeenAt = DateTimeOffset.UtcNow
        };

        // Find outputs that match our tracked addresses
        for (var i = 0; i < tx.Outputs.Count; i++)
        {
            var output = tx.Outputs[i];
            var scriptHash = ScriptHashUtility.ComputeScriptHash(output.ScriptPubKey);

            // Check if this output goes to one of our addresses
            if (scriptHash == matchedAddr.Scripthash ||
                _subscribedScripthashes.ContainsKey(scriptHash))
            {
                var parts = matchedAddr.KeyPath.Split('/');
                var keyIndex = parts.Length == 2 && int.TryParse(parts[1], out var idx) ? idx : 0;

                info.Outputs.Add(new OutputInfo
                {
                    Address = matchedAddr.Address,
                    Index = i,
                    Value = output.Value,
                    KeyPath = matchedAddr.KeyPath,
                    KeyIndex = keyIndex
                });
            }
        }

        return info.Outputs.Count > 0 ? info : null;
    }

    private ConcurrentDictionary<string, string> _subscribedScripthashes =>
        (ConcurrentDictionary<string, string>)typeof(ElectrumClient)
            .GetField("_subscribedScripthashes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(_client) ?? new ConcurrentDictionary<string, string>();
}
