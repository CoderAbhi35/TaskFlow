# syntax=docker/dockerfile:1
#
# Images for the .NET services, built from the repository root. Pick one with --target:
#
#   docker build -f deploy/docker/Dockerfile --target api       -t reeve-api .
#   docker build -f deploy/docker/Dockerfile --target worker    -t reeve-worker .
#   docker build -f deploy/docker/Dockerfile --target scheduler -t reeve-scheduler .
#   docker build -f deploy/docker/Dockerfile --target migrate   -t reeve-migrate .
#
# Runtime images are Ubuntu "chiseled": no shell or package manager, non-root by default. The
# "extra" variant adds ICU and time zone data, which schedules need for their time zones. All four
# images use the ASP.NET runtime: the worker and scheduler serve their health probes over HTTP,
# and the migration bundle is built from Infrastructure, which hosts those endpoints.
# Every service listens on 8080.

ARG DOTNET_VERSION=10.0

# ---- build --------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
# Restoring and publishing for the image's architecture keeps only that platform's native libraries
# (librdkafka ships builds for Windows, macOS and several Linux targets: about 70 MB otherwise).
ARG TARGETARCH
WORKDIR /src

# Restore from the project files alone, so this layer is reused until a dependency changes.
COPY dotnet-tools.json ./
COPY src/Reeve.Domain/Reeve.Domain.csproj                 src/Reeve.Domain/
COPY src/Reeve.Contracts/Reeve.Contracts.csproj           src/Reeve.Contracts/
COPY src/Reeve.Application/Reeve.Application.csproj       src/Reeve.Application/
COPY src/Reeve.Infrastructure/Reeve.Infrastructure.csproj src/Reeve.Infrastructure/
COPY src/Reeve.Api/Reeve.Api.csproj                       src/Reeve.Api/
COPY src/Reeve.Worker/Reeve.Worker.csproj                 src/Reeve.Worker/
COPY src/Reeve.Scheduler/Reeve.Scheduler.csproj           src/Reeve.Scheduler/
RUN dotnet restore src/Reeve.Api/Reeve.Api.csproj -a $TARGETARCH \
 && dotnet restore src/Reeve.Worker/Reeve.Worker.csproj -a $TARGETARCH \
 && dotnet restore src/Reeve.Scheduler/Reeve.Scheduler.csproj -a $TARGETARCH \
 && dotnet tool restore

COPY src/ src/

FROM build AS publish-api
RUN dotnet publish src/Reeve.Api -c Release -a $TARGETARCH --self-contained false --no-restore -o /out -p:UseAppHost=false

FROM build AS publish-worker
RUN dotnet publish src/Reeve.Worker -c Release -a $TARGETARCH --self-contained false --no-restore -o /out -p:UseAppHost=false

FROM build AS publish-scheduler
RUN dotnet publish src/Reeve.Scheduler -c Release -a $TARGETARCH --self-contained false --no-restore -o /out -p:UseAppHost=false

# An EF Core migration bundle: one executable that applies pending migrations and exits. It reads
# the connection string from REEVE_CONNECTION_STRING (see DesignTimeDbContextFactory).
FROM build AS publish-migrate
RUN dotnet ef migrations bundle --configuration Release \
      --project src/Reeve.Infrastructure --startup-project src/Reeve.Infrastructure \
      -o /out/efbundle

# ---- runtime ------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra AS api
WORKDIR /app
COPY --from=publish-api /out .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Reeve.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra AS worker
WORKDIR /app
COPY --from=publish-worker /out .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Reeve.Worker.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra AS scheduler
WORKDIR /app
COPY --from=publish-scheduler /out .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Reeve.Scheduler.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra AS migrate
WORKDIR /app
COPY --from=publish-migrate /out/efbundle .
# The bundle is a single-file app that unpacks itself first; /tmp is writable even when the root
# filesystem is mounted read-only.
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp
ENTRYPOINT ["./efbundle"]
