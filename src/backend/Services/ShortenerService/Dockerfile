FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/backend/Services/ShortenerService/ShortenerService.csproj", "src/backend/Services/ShortenerService/"]
COPY ["src/backend/Shared/Contracts/Contracts.csproj", "src/backend/Shared/Contracts/"]
COPY ["src/backend/Shared/Infrastructure/Infrastructure.csproj", "src/backend/Shared/Infrastructure/"]
COPY ["src/backend/Shared/Common/Common.csproj", "src/backend/Shared/Common/"]
COPY ["src/backend/Shared/ServiceDefaults/ServiceDefaults.csproj", "src/backend/Shared/ServiceDefaults/"]
RUN dotnet restore "src/backend/Services/ShortenerService/ShortenerService.csproj"
COPY . .
WORKDIR "/src/src/backend/Services/ShortenerService"
RUN dotnet build "ShortenerService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ShortenerService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ShortenerService.dll"]
