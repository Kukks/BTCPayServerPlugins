dotnet publish BTCPayServer.Plugins.Wabisabi.csproj -c Release -o bin/publish/BTCPayServer.Plugins.Wabisabi
dotnet run -p ../../submodules/btcpayserver/BTCPayServer.PluginPacker bin/publish/BTCPayServer.Plugins.Wabisabi BTCPayServer.Plugins.Wabisabi ../packed
