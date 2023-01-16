dotnet publish -c Altcoins-Release -o bin/publish/BTCPayServer.Plugins.Wabisabi
dotnet run -p ../../BTCPayServer.PluginPacker bin/publish/BTCPayServer.Plugins.Wabisabi BTCPayServer.Plugins.Wabisabi ../packed
