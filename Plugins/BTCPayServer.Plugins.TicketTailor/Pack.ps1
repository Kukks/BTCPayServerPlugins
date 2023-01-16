dotnet publish -c Altcoins-Release -o bin/publish/BTCPayServer.Plugins.TicketTailor
dotnet run -p ../../BTCPayServer.PluginPacker bin/publish/BTCPayServer.Plugins.TicketTailor BTCPayServer.Plugins.TicketTailor ../packed
