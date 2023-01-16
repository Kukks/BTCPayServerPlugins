dotnet publish -c Altcoins-Release -o bin/publish/BTCPayServer.Plugins.FixedFloat
dotnet run -p ../../BTCPayServer.PluginPacker bin/publish/BTCPayServer.Plugins.FixedFloat BTCPayServer.Plugins.FixedFloat ../packed
