FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["Plugins/BTCPayServer.Plugins.NIP05/BTCPayServer.Plugins.NIP05.csproj", "Plugins/BTCPayServer.Plugins.NIP05/"]
RUN dotnet restore "Plugins/BTCPayServer.Plugins.NIP05/BTCPayServer.Plugins.NIP05.csproj"
COPY . .
WORKDIR "/src/Plugins/BTCPayServer.Plugins.NIP05"
RUN dotnet build "BTCPayServer.Plugins.NIP05.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "BTCPayServer.Plugins.NIP05.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BTCPayServer.Plugins.NIP05.dll"]
