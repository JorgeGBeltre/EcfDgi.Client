FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
RUN apk add --no-cache icu-libs tzdata && \
    mkdir -p /app/certificates && \
    addgroup -g 1000 appgroup && \
    adduser -u 1000 -G appgroup -s /bin/sh -D appuser && \
    chown -R appuser:appgroup /app
ENV TZ=America/Santo_Domingo \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /app
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/EcfDgii.Client.Api/EcfDgii.Client.Api.csproj", "src/EcfDgii.Client.Api/"]
COPY ["src/EcfDgii.Client.Infrastructure/EcfDgii.Client.Infrastructure.csproj", "src/EcfDgii.Client.Infrastructure/"]
COPY ["src/EcfDgii.Client.Domain/EcfDgii.Client.Domain.csproj", "src/EcfDgii.Client.Domain/"]
COPY ["src/EcfDgii.Client.Shared/EcfDgii.Client.Shared.csproj", "src/EcfDgii.Client.Shared/"]
COPY ["src/EcfDgii.Client.Application/EcfDgii.Client.Application.csproj", "src/EcfDgii.Client.Application/"]
RUN dotnet restore "./src/EcfDgii.Client.Api/EcfDgii.Client.Api.csproj"
COPY . .
WORKDIR "/src/src/EcfDgii.Client.Api"
RUN dotnet build "./EcfDgii.Client.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./EcfDgii.Client.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish --chown=appuser:appgroup /app/publish .
USER appuser
ENTRYPOINT ["dotnet", "EcfDgii.Client.Api.dll"]
