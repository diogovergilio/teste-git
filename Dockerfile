FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/DashCall.Contracts ./DashCall.Contracts
COPY src/DashCall.Collector ./DashCall.Collector
RUN dotnet publish DashCall.Collector -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
# Config por variaveis de ambiente DASHCALL_* (ver .env.example). Sem segredo na imagem.
ENTRYPOINT ["dotnet", "DashCall.Collector.dll"]
