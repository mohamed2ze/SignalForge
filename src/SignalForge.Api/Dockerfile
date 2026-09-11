#------------------------------------------------------------------
# SignalForge API image — sdk build stage -> aspnet runtime stage
#------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first so dependency layers cache well.
COPY src/SignalForge.Domain/SignalForge.Domain.csproj src/SignalForge.Domain/
COPY src/SignalForge.Application/SignalForge.Application.csproj src/SignalForge.Application/
COPY src/SignalForge.Infrastructure/SignalForge.Infrastructure.csproj src/SignalForge.Infrastructure/
COPY src/SignalForge.Api/SignalForge.Api.csproj src/SignalForge.Api/
RUN dotnet restore src/SignalForge.Api/SignalForge.Api.csproj

COPY src/ ./src/
RUN dotnet publish src/SignalForge.Api/SignalForge.Api.csproj \
    -c Release -o /app/publish --no-restore

#------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080

# curl is used by the container healthcheck (/health/live, /health/ready).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "SignalForge.Api.dll"]