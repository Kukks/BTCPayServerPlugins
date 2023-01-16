FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["ConfigBuilder/ConfigBuilder.csproj", "ConfigBuilder/"]
RUN dotnet restore "ConfigBuilder/ConfigBuilder.csproj"
COPY . .
WORKDIR "/src/ConfigBuilder"
RUN dotnet build "ConfigBuilder.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ConfigBuilder.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ConfigBuilder.dll"]
