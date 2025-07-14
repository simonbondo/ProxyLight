FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Install NativeAOT build deps
RUN apt-get update \
  && apt-get install -y --no-install-recommends \
  clang \
  zlib1g-dev

WORKDIR /source
COPY . .

RUN dotnet publish ./ProxyLight/ --disable-build-servers --sc -c Release -r linux-x64 -o /app \
  && rm /app/*.dbg \
  && rm /app/appsettings.Development.json \
  && mkdir -p /app/cache

FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .

ARG UID=1000
ARG GID=1000
ARG HOST="0.0.0.0"
ARG PORT=5000

ENV HTTP_PORTS=${PORT}
ENV ASPNETCORE_URLS=http://${HOST}:${HTTP_PORTS}
ENV DOTNET_ENVIRONMENT=Production
ENV PROXYLIGHT__CACHE__ENABLED=false
ENV PROXYLIGHT__CACHE__PATH=/app/cache
ENV PROXYLIGHT__CACHE__SLIDINGAGE=00:05:00

USER ${UID}:${GID}
EXPOSE ${PORT}
VOLUME [ "/app/cache" ]
ENTRYPOINT [ "/app/ProxyLight" ]
