FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Services/RedirectApi/RedirectApi.csproj", "src/Services/RedirectApi/"]
COPY ["src/Shared/Contracts/Contracts.csproj", "src/Shared/Contracts/"]
COPY ["src/Shared/Infrastructure/Infrastructure.csproj", "src/Shared/Infrastructure/"]
COPY ["src/Shared/Common/Common.csproj", "src/Shared/Common/"]
RUN dotnet restore "src/Services/RedirectApi/RedirectApi.csproj"
COPY . .
WORKDIR "/src/src/Services/RedirectApi"
RUN dotnet build "RedirectApi.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "RedirectApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RedirectApi.dll"]
