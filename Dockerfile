FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY AuraUpBack.sln ./
COPY src/AuraUpBack.Domain/AuraUpBack.Domain.csproj src/AuraUpBack.Domain/
COPY src/AuraUpBack.Application/AuraUpBack.Application.csproj src/AuraUpBack.Application/
COPY src/AuraUpBack.Infrastructure/AuraUpBack.Infrastructure.csproj src/AuraUpBack.Infrastructure/
COPY src/AuraUpBack.Api/AuraUpBack.Api.csproj src/AuraUpBack.Api/
COPY src/AuraUpBack.Worker/AuraUpBack.Worker.csproj src/AuraUpBack.Worker/

RUN dotnet restore AuraUpBack.sln

COPY src/ ./src/

RUN dotnet publish src/AuraUpBack.Api/AuraUpBack.Api.csproj \
    -c Release \
    -o /app/publish \
    -r linux-x64 \
    --self-contained true \
    /p:PublishSingleFile=false \
    --no-restore

FROM mcr.microsoft.com/playwright/dotnet:v1.55.0-noble AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0
ENV PLAYWRIGHT_DRIVER_SEARCH_PATH=/app
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
ENV AuraUpBack__DataPath=/app/App_Data/aura-up-back.json
ENV Instagram__RpaSessionStatePath=/app/App_Data/instagram-rpa-session.json
ENV Instagram__RpaHeadless=true

COPY --from=build /app/publish ./

RUN chmod +x /app/AuraUpBack.Api \
    && if [ -d /app/.playwright ]; then chmod -R a+rX /app/.playwright; fi \
    && if [ -f /app/.playwright/node/linux-x64/node ]; then chmod +x /app/.playwright/node/linux-x64/node; fi \
    && mkdir -p /app/App_Data /app/artifacts/rpa \
    && chown -R pwuser:pwuser /app /ms-playwright

USER pwuser

EXPOSE 8080

ENTRYPOINT ["sh", "-c", "./AuraUpBack.Api --urls http://0.0.0.0:${PORT:-8080}"]
