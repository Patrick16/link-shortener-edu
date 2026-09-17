FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Services/TrafficService/TrafficService.csproj", "src/Services/TrafficService/"]
COPY ["src/Shared/Contracts/Contracts.csproj", "src/Shared/Contracts/"]
COPY ["src/Shared/Infrastructure/Infrastructure.csproj", "src/Shared/Infrastructure/"]
COPY ["src/Shared/Common/Common.csproj", "src/Shared/Common/"]
COPY ["src/Shared/ServiceDefaults/ServiceDefaults.csproj", "src/Shared/ServiceDefaults/"]
RUN dotnet restore "src/Services/TrafficService/TrafficService.csproj"
COPY . .
WORKDIR "/src/src/Services/TrafficService"
RUN dotnet build "TrafficService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "TrafficService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "TrafficService.dll"]
