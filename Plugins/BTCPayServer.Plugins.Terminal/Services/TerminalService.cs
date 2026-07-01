using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.Terminal.Services;

public class TerminalService
{
    private readonly ConcurrentDictionary<string, TerminalState> _terminals = new();

    // Check-in is scoped per store so a single browser can act as a terminal in each
    // store independently, without one store's check-in overwriting another's.
    public static string CheckInCookieName(string storeId) => $"btcpay-terminal-{storeId}";

    public void RegisterTerminals(string appId, string storeId, IEnumerable<TerminalData> terminals)
    {
        var currentKeys = _terminals.Where(kv => kv.Value.AppId == appId).Select(kv => kv.Key).ToList();
        foreach (var key in currentKeys)
            _terminals.TryRemove(key, out _);

        foreach (var t in terminals)
        {
            _terminals[t.Id] = new TerminalState
            {
                TerminalId = t.Id,
                Name = t.Name,
                AppId = appId,
                StoreId = storeId
            };
        }
    }

    public void UnregisterApp(string appId)
    {
        var keys = _terminals.Where(kv => kv.Value.AppId == appId).Select(kv => kv.Key).ToList();
        foreach (var key in keys)
            _terminals.TryRemove(key, out _);
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

    public TerminalState GetTerminal(string terminalId)
    {
        _terminals.TryGetValue(terminalId, out var state);
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
}
