# syntax=docker/dockerfile:1
#
# Build context is the repository root:
#   docker build -f deploy/Dockerfile .

# --- web: build the SPA -----------------------------------------------------
FROM node:26-alpine AS web-build
WORKDIR /web
# .npmrc must be in place before `npm ci` — it carries legacy-peer-deps=true, without which
# the install fails on openapi-typescript's TypeScript peer range (see web/.npmrc).
COPY web/package.json web/package-lock.json web/.npmrc ./
RUN npm ci
COPY web/ .
RUN npm run build

# --- api: publish the ASP.NET Core app --------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props PartyUp.sln ./
COPY src/ src/
RUN dotnet publish src/PartyUp.Api -c Release -o /app/publish

# --- final: runtime image ---------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=api-build /app/publish .
COPY --from=web-build /web/dist ./wwwroot
# Docker gives a fresh named volume the ownership of its mount point in the image. Without this
# the dataprotection volume lands root-owned and the non-root `app` user cannot persist keys.
RUN mkdir -p /data/dataprotection && chown app:app /data/dataprotection
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "PartyUp.Api.dll"]
