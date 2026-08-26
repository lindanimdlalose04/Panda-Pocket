# Panda Pocket Merchant service.
#
# IMPORTANT: the build context is the repository root, not this directory,
# because this project references src/Shared/Contracts. Compose is configured
# accordingly:
#
#   build:
#     context: .
#     dockerfile: src/Services/Merchant.Api/Dockerfile
#
# Building from the service directory fails during restore with a missing
# project reference, which reads like a corrupt solution rather than a context
# problem.

# ---------------------------------------------------------------------------
# Build stage: the SDK image, which is large and never shipped.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the project files first and restore. Docker caches this layer, so
# editing source does not re-download every NuGet package. Copying everything up
# front would invalidate the cache on every source change.
COPY ["src/Shared/Contracts/PandaPocket.Shared.Contracts.csproj", "src/Shared/Contracts/"]
COPY ["src/Services/Merchant.Api/PandaPocket.Services.Merchant.csproj", "src/Services/Merchant.Api/"]
RUN dotnet restore "src/Services/Merchant.Api/PandaPocket.Services.Merchant.csproj"

COPY src/ src/
RUN dotnet publish "src/Services/Merchant.Api/PandaPocket.Services.Merchant.csproj" \
    -c Release -o /app/publish --no-restore

# ---------------------------------------------------------------------------
# Runtime stage: the ASP.NET image, roughly a fifth of the size of the SDK and
# with no compiler or package cache present.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# curl is needed so compose can probe /health. The ASP.NET image ships without
# it. Later services depend on Rate being healthy rather than merely started,
# and that condition needs a probe that can make an HTTP request.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Run as the non-root user the .NET images provide, rather than as root.
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "PandaPocket.Services.Merchant.dll"]
