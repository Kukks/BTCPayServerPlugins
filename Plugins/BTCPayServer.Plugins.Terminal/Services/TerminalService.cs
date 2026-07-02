using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BTCPayServer.Plugins.Terminal.Services;

public class TerminalService
{
    private readonly ConcurrentDictionary<string, TerminalState> _terminals = new();
    private readonly ConcurrentDictionary<string, TerminalState> _byCustomerHash = new();

    // Check-in is scoped per store so a single browser can act as a terminal in each
    // store independently, without one store's check-in overwriting another's.
    public static string CheckInCookieName(string storeId) => $"btcpay-terminal-{storeId}";

    // The customer-facing URL uses a one-way hash of the terminal id, so that seeing it
    // (it is the widely-distributed tag) never reveals the raw id used by the cashier
    // check-in URL.
    public static string CustomerHash(string terminalId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(terminalId));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public void RegisterTerminals(string appId, string storeId, IEnumerable<TerminalData> terminals)
    {
        RemoveApp(appId);
        foreach (var t in terminals)
        {
            var state = new TerminalState
            {
                TerminalId = t.Id,
                Name = t.Name,
                AppId = appId,
                StoreId = storeId,
                CustomerHash = CustomerHash(t.Id)
            };
            _terminals[t.Id] = state;
            _byCustomerHash[state.CustomerHash] = state;
        }
    }

    public void UnregisterApp(string appId) => RemoveApp(appId);

    private void RemoveApp(string appId)
    {
        foreach (var kv in _terminals.Where(kv => kv.Value.AppId == appId).ToList())
        {
            _terminals.TryRemove(kv.Key, out _);
            if (kv.Value.CustomerHash != null)
                _byCustomerHash.TryRemove(kv.Value.CustomerHash, out _);
        }
    }

    public void SetCurrentInvoice(string terminalId, string invoiceId)
    {
        if (_terminals.TryGetValue(terminalId, out var state))
            state.CurrentInvoiceId = invoiceId;
    }

    public bool ClearInvoice(string invoiceId)
    {
        foreach (var state in _terminals.Values)
        {
            if (state.CurrentInvoiceId == invoiceId)
            {
                state.CurrentInvoiceId = null;
                return true;
            }
        }
        return false;
    }

    // Clears whatever invoice a terminal is currently serving (the cashier's "Clear"
    // button), regardless of which invoice id it is.
    public bool ClearTerminal(string terminalId)
    {
        if (_terminals.TryGetValue(terminalId, out var state) && state.CurrentInvoiceId != null)
        {
            state.CurrentInvoiceId = null;
            return true;
        }
        return false;
    }

    public TerminalState GetTerminal(string terminalId)
    {
        _terminals.TryGetValue(terminalId, out var state);
        return state;
    }

    public TerminalState GetTerminalByCustomerHash(string hash)
    {
        _byCustomerHash.TryGetValue(hash, out var state);
        return state;
    }

    public IEnumerable<TerminalState> GetTerminalsForApp(string appId)
    {
        return _terminals.Values.Where(t => t.AppId == appId);
    }
}

public class TerminalState
{
    public string TerminalId { get; set; }
    public string Name { get; set; }
    public string AppId { get; set; }
    public string StoreId { get; set; }
    public string CurrentInvoiceId { get; set; }
    public string CustomerHash { get; set; }
}
