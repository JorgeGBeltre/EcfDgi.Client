FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "EcfDgii.Client.Api.dll"]
