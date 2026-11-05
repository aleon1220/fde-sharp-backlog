# Stage 1: Build & Publish (.NET 10 SDK)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/SmartBacklogAssistant/SmartBacklogAssistant.csproj", "src/SmartBacklogAssistant/"]
RUN dotnet restore "src/SmartBacklogAssistant/SmartBacklogAssistant.csproj"

COPY ["src/SmartBacklogAssistant/", "src/SmartBacklogAssistant/"]
RUN dotnet publish "src/SmartBacklogAssistant/SmartBacklogAssistant.csproj" -c Release -o /app/publish /p:UseAppHost=false

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
