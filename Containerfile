# Stage 1: Build & Publish (.NET 10 SDK)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

ARG NUGET_PROXY_FEED_URL=""
ARG NUGET_PROXY_ONLY="false"
ENV NUGET_ENABLE_ENHANCED_HTTP_RETRY=true \
    NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT=6 \
    NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS=1000 \
    NUGET_RETRY_HTTP_429=true \
    NUGET_OBSERVE_RETRY_AFTER=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    NUGET_PROXY_FEED_URL="${NUGET_PROXY_FEED_URL}" \
    NUGET_PROXY_ONLY="${NUGET_PROXY_ONLY}"

COPY ["Directory.Build.props", "nuget.config", "./"]
COPY ["prompts/", "prompts/"]
COPY ["src/SmartBacklogAssistant/SmartBacklogAssistant.csproj", "src/SmartBacklogAssistant/packages.lock.json", "src/SmartBacklogAssistant/"]
RUN dotnet restore "src/SmartBacklogAssistant/SmartBacklogAssistant.csproj" --configfile nuget.config --locked-mode

COPY ["src/SmartBacklogAssistant/", "src/SmartBacklogAssistant/"]
RUN dotnet publish "src/SmartBacklogAssistant/SmartBacklogAssistant.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Stage 2: Runtime (.NET 10 Runtime)
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./
COPY prompts/ ./prompts/
COPY samples/ ./samples/

RUN mkdir -p /app/output && chown -R app:app /app
USER app

ENTRYPOINT ["dotnet", "SmartBacklogAssistant.dll"]
CMD ["--input", "samples/raw-notes.txt", "--prompt", "prompts/as-is-v1-prompt.md", "--non-interactive"]
