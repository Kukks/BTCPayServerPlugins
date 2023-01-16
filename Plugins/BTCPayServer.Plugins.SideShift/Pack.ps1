dotnet publish -c Altcoins-Release -o bin/publish/BTCPayServer.Plugins.SideShift
dotnet run -p ../../BTCPayServer.PluginPacker bin/publish/BTCPayServer.Plugins.SideShift BTCPayServer.Plugins.SideShift ../packed
