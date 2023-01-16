dotnet publish -c Altcoins-Release -o bin/publish/BTCPayServer.Plugins.LiquidPlus
dotnet run -p ../../BTCPayServer.PluginPacker bin/publish/BTCPayServer.Plugins.LiquidPlus BTCPayServer.Plugins.LiquidPlus ../packed
