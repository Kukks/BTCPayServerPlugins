using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using NBitcoin;
using Newtonsoft.Json.Linq;
using WalletWasabi.WabiSabi.Backend;

namespace BTCPayServer.Plugins.Wabisabi.Coordinator;

public class WabisabiScriptResolver: WabiSabiConfig.CoordinatorScriptResolver
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly BTCPayWalletProvider _walletProvider;

    public WabisabiScriptResolver(IHttpClientFactory httpClientFactory, 
        StoreRepository storeRepository, 
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
        BTCPayNetworkProvider networkProvider, 
        BTCPayWalletProvider walletProvider) 
    {
        _httpClientFactory = httpClientFactory;
        _storeRepository = storeRepository;
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
        _networkProvider = networkProvider;
        _walletProvider = walletProvider;
    }

    private static async Task<string> GetRedirectedUrl(HttpClient client, string url,
        CancellationToken cancellationToken)
    {
        var redirectedUrl = url;
        using var response = await client.PostAsync(url, new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()), cancellationToken).ConfigureAwait(false);
        using var content = response.Content;
        // ... Read the response to see if we have the redirected url
        if (response.StatusCode == System.Net.HttpStatusCode.Found)
        {
            var headers = response.Headers;
            if (headers.Location != null)
            {
                redirectedUrl = new Uri(new Uri(url), headers.Location.ToString()).ToString();
            }
        }

        return redirectedUrl;
    }
	
    public override async Task<Script> ResolveScript(string type, string value, Network network, CancellationToken cancellationToken)
    {

        using var  httpClient = _httpClientFactory.CreateClient("wabisabi-coordinator-scripts-no-redirect.onion");
        string? invoiceUrl = null;
        switch (type)
        {
            case "store":
                var store = await _storeRepository.FindStore(value);
                var cryptoCode = _networkProvider.GetAll().OfType<BTCPayNetwork>()
                    .First(payNetwork => payNetwork.NBitcoinNetwork == network);
                var dss = store.GetDerivationSchemeSettings(_paymentMethodHandlerDictionary, cryptoCode.CryptoCode);
                var w = _walletProvider.GetWallet(cryptoCode.CryptoCode);
                var kpi = await w.ReserveAddressAsync(store.Id, dss.AccountDerivation, "wabisabi coordinator");
                return kpi.ScriptPubKey;
            case "hrf":
                return await ResolveScript("btcpaybutton", "https://btcpay.hrf.org/api/v1/invoices?storeId=BgQWsm5WmU9qDPbZVgxVYZu3hWJsbnAtJ3f7wc56b1fC&currency=BTC&jsonResponse=true", network, cancellationToken).ConfigureAwait(false);
            case "btcpaybutton":
                var buttonResult = await httpClient.GetAsync(value, cancellationToken).ConfigureAwait(false);
                var c = await buttonResult.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                invoiceUrl = JObject.Parse(c).Value<string>("invoiceUrl");
                break;
            case "dev":
                return await ResolveScript("btcpaypos", "https://btcpay.kukks.org/apps/4NmbS9jCAEHyPqtaynSXeqNm1hgC/pos", network, cancellationToken).ConfigureAwait(false);
            case "btcpaypos":
                invoiceUrl = await GetRedirectedUrl(httpClient, value, cancellationToken).ConfigureAwait(false);
                break;
            case "opensats":
            {
                if (string.IsNullOrEmpty(value))
                {
                    value = "btcpayserver";
                }
                var content = new StringContent(JObject.FromObject(new
                {
                    btcpay = value,
                    name = "kukks <3 you"
                }).ToString(), Encoding.UTF8, "application/json");
                var result = await httpClient.PostAsync("https://opensats.org/api/btcpay",content, cancellationToken).ConfigureAwait(false);

                var rawInvoice = JObject.Parse(await result.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                invoiceUrl = rawInvoice.Value<string>("checkoutLink");

                break;
            }
        }

        invoiceUrl = invoiceUrl.TrimEnd('/');
        invoiceUrl += "/BTC/status";
        var invoiceBtcpayModel = JObject.Parse(await httpClient.GetStringAsync(invoiceUrl, cancellationToken).ConfigureAwait(false));
        var btcAddress = invoiceBtcpayModel.Value<string>("btcAddress");
        foreach (var n in Network.GetNetworks())
        {
            try
            {

                return BitcoinAddress.Create(btcAddress, n).ScriptPubKey;
            }
            catch (Exception e)
            {
            }
        }

        return null;
    }
}