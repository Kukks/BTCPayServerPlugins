dotnet publish -c Release -o bin/publish/BTCPayServer.Plugins.DataErasure
dotnet run -p ../../BTCPayServer.PluginPacker bin/publish/BTCPayServer.Plugins.DataErasure BTCPayServer.Plugins.DataErasure ../packed
