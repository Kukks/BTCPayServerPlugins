dotnet publish -c Altcoins-Release -o bin/publish/BTCPayServer.Plugins.AOPP
dotnet run -p ../../BTCPayServer.PluginPacker bin/publish/BTCPayServer.Plugins.AOPP BTCPayServer.Plugins.AOPP ../packed
