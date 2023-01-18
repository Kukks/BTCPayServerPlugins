dotnet publish -c Altcoins-Release -o bin/Altcoins-Debug/net6.0
dotnet run -p ../../submodules/btcpayserver/BTCPayServer.PluginPacker bin/publish/BTCPayServer.Plugins.Wabisabi BTCPayServer.Plugins.Wabisabi ../packed
