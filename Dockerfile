# Build context must be the repository root (see docs/railway-deployment.md).
# Catalogue/content seeders resolve datasets from tools/*-migrate/out beside the
# published content root; without COPY tools/, publish succeeds empty and the
# API exits in DbSeeder at boot.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY backend/ ./backend/
COPY tools/catalogue-migrate/out/ ./tools/catalogue-migrate/out/
COPY tools/content-migrate/out/ ./tools/content-migrate/out/
WORKDIR /src/backend
RUN dotnet restore src/Stockpile.Api/Stockpile.Api.csproj
RUN dotnet publish src/Stockpile.Api/Stockpile.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "Stockpile.Api.dll"]
