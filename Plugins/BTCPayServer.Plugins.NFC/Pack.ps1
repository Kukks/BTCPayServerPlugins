dotnet publish -c Altcoins-Release -o bin/publish/BTCPayServer.Plugins.NFC
dotnet run -p ../../BTCPayServer.PluginPacker bin/publish/BTCPayServer.Plugins.NFC BTCPayServer.Plugins.NFC ../packed
