FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Install NativeAOT build deps
RUN apt-get update \
  && apt-get install -y --no-install-recommends \
  clang \
  zlib1g-dev

WORKDIR /source
COPY . .

RUN dotnet publish ./ProxyLight/ -r linux-x64 -c Release -o /app \
  && rm /app/*.dbg

FROM mcr.microsoft.com/dotnet/runtime-deps:9.0 AS deps
# /usr/bin must be last, because it removes the rm command
#RUN rm -rf /var \
#  && rm -rf /root \
#  && rm -rf /home \
#  && rm /sbin \
#  && rm -rf /usr/sbin \
#  && rm -rf /usr/share \
#  && rm -rf /usr/libexec \
#  && rm /bin \
#  && rm -rf /usr/bin
#
#FROM scratch
#COPY --from=deps / /
# Using FROM scratch, the app can start, but doesn't serve any requests - probably some of the removed files are needed at runtime

WORKDIR /app
COPY --from=build /app .

USER 1000:1000
EXPOSE 8080
ENTRYPOINT [ "/app/ProxyLight" ]
