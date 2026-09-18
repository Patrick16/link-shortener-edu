FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/backend/Services/RedirectApi/RedirectApi.csproj", "src/backend/Services/RedirectApi/"]
COPY ["src/backend/Shared/Contracts/Contracts.csproj", "src/backend/Shared/Contracts/"]
COPY ["src/backend/Shared/Infrastructure/Infrastructure.csproj", "src/backend/Shared/Infrastructure/"]
COPY ["src/backend/Shared/Common/Common.csproj", "src/backend/Shared/Common/"]
COPY ["src/backend/Shared/ServiceDefaults/ServiceDefaults.csproj", "src/backend/Shared/ServiceDefaults/"]
RUN dotnet restore "src/backend/Services/RedirectApi/RedirectApi.csproj"
COPY . .
WORKDIR "/src/src/backend/Services/RedirectApi"
RUN dotnet build "RedirectApi.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "RedirectApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RedirectApi.dll"]
