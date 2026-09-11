#------------------------------------------------------------------
# SignalForge Worker image — sdk build stage -> runtime stage
#------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/SignalForge.Domain/SignalForge.Domain.csproj src/SignalForge.Domain/
COPY src/SignalForge.Application/SignalForge.Application.csproj src/SignalForge.Application/
COPY src/SignalForge.Infrastructure/SignalForge.Infrastructure.csproj src/SignalForge.Infrastructure/
COPY src/SignalForge.Worker/SignalForge.Worker.csproj src/SignalForge.Worker/
RUN dotnet restore src/SignalForge.Worker/SignalForge.Worker.csproj

COPY src/ ./src/
RUN dotnet publish src/SignalForge.Worker/SignalForge.Worker.csproj \
    -c Release -o /app/publish --no-restore

#------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SignalForge.Worker.dll"]