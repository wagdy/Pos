# syntax=docker/dockerfile:1
#
# The restaurant's till: the till server (OtantikPos.Node.Api) with the till app built into it,
# as one image. Build context is the repository root, which holds the order model the till
# shares with the delivery system:
#   docker build -f otantik-pos/Dockerfile -t otantik-till .
# It runs beside its own PostgreSQL: see deploy/docker-compose.yml and deploy/INSTALL.md.
# What reaches the build is listed in Dockerfile.dockerignore, next to this file.

# ---- The till app (Angular) ----
FROM node:22-alpine AS app
WORKDIR /src/otantik-pos/node-frontend
COPY otantik-pos/node-frontend/package.json otantik-pos/node-frontend/package-lock.json ./
RUN npm ci
COPY otantik-pos/node-frontend/ ./
# Writes into ../backend/src/OtantikPos.Node.Api/wwwroot (angular.json), as in development.
RUN npm run build

# ---- The till server (.NET) ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /src

# Project files first, so the restore layer survives source edits.
COPY shared/Otantik.SharedKernel/Otantik.SharedKernel.csproj shared/Otantik.SharedKernel/
COPY shared/Otantik.BuildingBlocks/Otantik.BuildingBlocks.csproj shared/Otantik.BuildingBlocks/
COPY otantik-pos/backend/src/OtantikPos.Ordering.Contracts/OtantikPos.Ordering.Contracts.csproj otantik-pos/backend/src/OtantikPos.Ordering.Contracts/
COPY otantik-pos/backend/src/OtantikPos.Ordering.Domain/OtantikPos.Ordering.Domain.csproj otantik-pos/backend/src/OtantikPos.Ordering.Domain/
COPY otantik-pos/backend/src/OtantikPos.Ordering.Application/OtantikPos.Ordering.Application.csproj otantik-pos/backend/src/OtantikPos.Ordering.Application/
COPY otantik-pos/backend/src/OtantikPos.Inventory.Domain/OtantikPos.Inventory.Domain.csproj otantik-pos/backend/src/OtantikPos.Inventory.Domain/
COPY otantik-pos/backend/src/OtantikPos.Inventory.Application/OtantikPos.Inventory.Application.csproj otantik-pos/backend/src/OtantikPos.Inventory.Application/
COPY otantik-pos/backend/src/OtantikPos.Node.Infrastructure/OtantikPos.Node.Infrastructure.csproj otantik-pos/backend/src/OtantikPos.Node.Infrastructure/
COPY otantik-pos/backend/src/OtantikPos.Node.Api/OtantikPos.Node.Api.csproj otantik-pos/backend/src/OtantikPos.Node.Api/
RUN dotnet restore otantik-pos/backend/src/OtantikPos.Node.Api/OtantikPos.Node.Api.csproj

COPY shared/Otantik.SharedKernel/ shared/Otantik.SharedKernel/
COPY shared/Otantik.BuildingBlocks/ shared/Otantik.BuildingBlocks/
COPY otantik-pos/backend/src/ otantik-pos/backend/src/
COPY --from=app /src/otantik-pos/backend/src/OtantikPos.Node.Api/wwwroot otantik-pos/backend/src/OtantikPos.Node.Api/wwwroot
RUN dotnet publish otantik-pos/backend/src/OtantikPos.Node.Api/OtantikPos.Node.Api.csproj \
    -c Release -o /app --no-restore /p:UseAppHost=false

# ---- Runtime ----
# The Debian image, not Alpine: it carries the time zone database the business day is counted
# in (Restaurant:TimeZoneId, Africa/Cairo) and ICU for dates and money.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# The PostgreSQL driver looks for Kerberos on start and logs an error when it is missing; it is
# not used, but an error in the log on day one sends someone looking for a problem.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=server /app .

ENV ASPNETCORE_ENVIRONMENT=Production
# The base image listens on 8080 by default; cleared so the till's own port applies without a
# warning about one overriding the other.
ENV ASPNETCORE_HTTP_PORTS=
ENV Urls=http://0.0.0.0:5080
EXPOSE 5080

# The base image's own non-root user.
USER $APP_UID
ENTRYPOINT ["dotnet", "OtantikPos.Node.Api.dll"]
