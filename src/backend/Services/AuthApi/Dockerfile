FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/backend/Services/AuthApi/AuthApi.csproj", "src/backend/Services/AuthApi/"]
COPY ["src/backend/Shared/Contracts/Contracts.csproj", "src/backend/Shared/Contracts/"]
COPY ["src/backend/Shared/Infrastructure/Infrastructure.csproj", "src/backend/Shared/Infrastructure/"]
COPY ["src/backend/Shared/Common/Common.csproj", "src/backend/Shared/Common/"]
COPY ["src/backend/Shared/ServiceDefaults/ServiceDefaults.csproj", "src/backend/Shared/ServiceDefaults/"]
RUN dotnet restore "src/backend/Services/AuthApi/AuthApi.csproj"
COPY . .
WORKDIR "/src/src/backend/Services/AuthApi"
RUN dotnet build "AuthApi.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "AuthApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AuthApi.dll"]
