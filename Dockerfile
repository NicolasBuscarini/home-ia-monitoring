FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json HomeIaMonitoring.slnx ./
COPY src/HomeIaMonitoring/HomeIaMonitoring.csproj src/HomeIaMonitoring/
COPY tests/HomeIaMonitoring.Tests/HomeIaMonitoring.Tests.csproj tests/HomeIaMonitoring.Tests/
RUN dotnet restore HomeIaMonitoring.slnx --locked-mode
COPY . .
RUN dotnet publish src/HomeIaMonitoring/HomeIaMonitoring.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b
ARG APP_UID=1000
ARG APP_GID=1000
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg=7:6.1.1-3ubuntu5 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=true \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_CLI_HOME=/tmp \
    NUGET_PACKAGES=/tmp/nuget \
    HOME=/tmp \
    ASPNETCORE_URLS=http://0.0.0.0:8080
USER ${APP_UID}:${APP_GID}
ENTRYPOINT ["dotnet", "HomeIaMonitoring.dll"]
