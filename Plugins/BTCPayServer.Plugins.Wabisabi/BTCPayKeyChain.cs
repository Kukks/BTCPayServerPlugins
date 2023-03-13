using System;
using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Crypto;
using WalletWasabi.Extensions;
using WalletWasabi.WabiSabi.Client;

namespace BTCPayServer.Plugins.Wabisabi;

public class BTCPayKeyChain : IKeyChain
{
    public Smartifier Smartifier { get; }
    private readonly ExplorerClient _explorerClient;
    private readonly DerivationStrategyBase _derivationStrategy;
    private readonly ExtKey _masterKey;
    private readonly ExtKey _accountKey;

    public bool KeysAvailable => _masterKey is not null && _accountKey is not null;

    public BTCPayKeyChain(ExplorerClient explorerClient, DerivationStrategyBase derivationStrategy, ExtKey masterKey,
        ExtKey accountKey, Smartifier smartifier)
    {
        Smartifier = smartifier;
        _explorerClient = explorerClient;
        _derivationStrategy = derivationStrategy;
        _masterKey = masterKey;
        _accountKey = accountKey;
    }


    public OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData committedData)
    {
        return NBitcoinExtensions.GetOwnershipProof(_masterKey.PrivateKey, GetBitcoinSecret(destination.ScriptPubKey),
            destination.ScriptPubKey, committedData);
    }

    public Transaction Sign(Transaction transaction, Coin coin, PrecomputedTransactionData precomputeTransactionData)
    {
        transaction = transaction.Clone();

        if (transaction.Inputs.Count == 0)
        {
            throw new ArgumentException("No inputs to sign.", nameof(transaction));
        }

        var txInput = transaction.Inputs.AsIndexedInputs().FirstOrDefault(input => input.PrevOut == coin.Outpoint);

        if (txInput is null)
        {
            throw new InvalidOperationException("Missing input.");
        }


        BitcoinSecret secret = GetBitcoinSecret(coin.ScriptPubKey);

        TransactionBuilder builder = Network.Main.CreateTransactionBuilder();
        builder.AddKeys(secret);
        builder.AddCoins(coin);
        builder.SetSigningOptions(new SigningOptions(TaprootSigHash.All,
            (TaprootReadyPrecomputedTransactionData)precomputeTransactionData));
        builder.SignTransactionInPlace(transaction);

        return transaction;
    }

    public void TrySetScriptStates(KeyState state, IEnumerable<Script> scripts)
    {
    }

    private BitcoinSecret GetBitcoinSecret(Script scriptPubKey)
    {
        var keyPath = _explorerClient.GetKeyInformation(_derivationStrategy, scriptPubKey).KeyPath;
        return _accountKey.Derive(keyPath).PrivateKey.GetBitcoinSecret(_explorerClient.Network.NBitcoinNetwork);
    }
}
