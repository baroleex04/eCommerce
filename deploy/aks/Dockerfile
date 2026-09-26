# SimplCommerce on AKS â€” clean .NET 8 image using SQL Server (the app's native
# provider, so NO source hacking). The app reads its config FROM the database at
# startup (EFConfigProvider), so the schema must exist before the app boots.
# We generate an idempotent EF migration SQL script at build time and apply it
# with sqlcmd from an initContainer (see deploy/aks/k8s/app.yaml).
# Build context = repo root:  docker build -f deploy/aks/Dockerfile .

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .

RUN dotnet tool install --global dotnet-ef --version 8.*
ENV PATH="${PATH}:/root/.dotnet/tools"

RUN dotnet restore SimplCommerce.sln

# SimplCommerce copies each module's assemblies into the WebHost content root
# during a build, and the design-time factory (MigrationSimplDbContextFactory â†’
# AddModules) loads them from Directory.GetCurrentDirectory(). So build the
# solution in Debug FIRST (populates the WebHost Modules/), then run `dotnet ef`
# FROM the WebHost dir. Emit an idempotent SQL script (NOT a bundle exe â€” the
# bundle self-build fails on this many-module solution). The "SQL Server not
# found" log line is a benign warning (EF tries the app host, then falls back to
# the design-time factory).
RUN dotnet build SimplCommerce.sln -c Debug
RUN cd src/SimplCommerce.WebHost \
    && dotnet ef migrations script --idempotent --context SimplDbContext -o /app/dbscript.sql

RUN dotnet publish src/SimplCommerce.WebHost/SimplCommerce.WebHost.csproj \
      -c Release -o /app/publish

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# libgdiplus â†’ System.Drawing; libwkhtmltox â†’ DinkToPdf (PDF); mssql-tools â†’ sqlcmd
# (the initContainer applies dbscript.sql before the app boots).
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgdiplus curl gnupg apt-transport-https ca-certificates \
    && curl -sSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/debian/12/prod bookworm main" > /etc/apt/sources.list.d/mssql-release.list \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y --no-install-recommends mssql-tools18 unixodbc \
    && rm -rf /var/lib/apt/lists/*
ENV PATH="$PATH:/opt/mssql-tools18/bin"

RUN curl -SL "https://github.com/rdvojmoc/DinkToPdf/raw/v1.0.8/v0.12.4/64%20bit/libwkhtmltox.so" \
      -o /app/libwkhtmltox.so

COPY --from=build /app/publish ./
COPY --from=build /app/dbscript.sql /app/dbscript.sql

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SimplCommerce.WebHost.dll"]
