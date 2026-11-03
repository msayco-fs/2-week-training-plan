# Invoice Reporting API.
# Build from the repo root:
#   docker build -f backend/InvoiceReporting.Api/Dockerfile -t invoice-reporting-api .
#
# No secrets or production settings are baked in. Connection strings, the
# Mailjet keys and the environment name come from environment variables at
# run time (see README).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# global.json is not copied: it pins the SDK feature band used on developer
# machines, which this image may not have. The image's .NET 10 SDK builds the
# same net10.0 app.

# Restore first, so this layer is cached until the project file changes.
COPY backend/InvoiceReporting.Api/InvoiceReporting.Api.csproj backend/InvoiceReporting.Api/
RUN dotnet restore backend/InvoiceReporting.Api/InvoiceReporting.Api.csproj

COPY backend/InvoiceReporting.Api/ backend/InvoiceReporting.Api/
RUN dotnet publish backend/InvoiceReporting.Api/InvoiceReporting.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /out \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The app's content root is /app, so the default settings resolve to:
#   logs              -> /app/logs     (failed report email log files)
#   reports/quarterly -> /app/reports/quarterly (quarterly CSV files)
# Both folders are owned by the non-root "app" user. A new named volume copies
# that owner, so it is writable without a chown on the host. A host bind
# mount needs the host folder owned by UID 1654.
RUN mkdir -p /app/logs /app/reports \
    && chown "$APP_UID:$APP_UID" /app/logs /app/reports

COPY --from=build /out .

# Mount persistent volumes here, or both are lost when the container is removed.
VOLUME ["/app/logs", "/app/reports"]

# APP_UID is the image's non-root "app" user (1654).
USER $APP_UID

# ASP.NET Core in this image listens on 8080 by default.
EXPOSE 8080

ENTRYPOINT ["dotnet", "InvoiceReporting.Api.dll"]
