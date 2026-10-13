FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
# The `docker` CLI + compose plugin, copied in rather than apt-installed - lets ScaleAsync shell
# out to `docker compose ... --scale` (the actual scaling logic lives in Compose itself, not
# reimplemented here against the raw Docker Engine API).
COPY --from=docker:27-cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker:27-cli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["sandbox/infra/control-api/ControlApi.csproj", "sandbox/infra/control-api/"]
# Central package management: csproj files have no Version attribute, so restore needs this
# file present before it can resolve package versions.
COPY ["Directory.Packages.props", "./"]
RUN dotnet restore "sandbox/infra/control-api/ControlApi.csproj"
COPY . .
WORKDIR "/src/sandbox/infra/control-api"
RUN dotnet build "ControlApi.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ControlApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY sandbox/infra/control-api/k6-scripts ./k6-scripts
ENTRYPOINT ["dotnet", "ControlApi.dll"]
