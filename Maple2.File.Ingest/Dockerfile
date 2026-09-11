FROM mcr.microsoft.com/dotnet/sdk:8.0

WORKDIR /app
COPY ["global.json", "./"]
COPY [".config/dotnet-tools.json", ".config/dotnet-tools.json"]
RUN dotnet tool restore

COPY . .
RUN dotnet build Maple2.File.Ingest/Maple2.File.Ingest.csproj -c Debug \
    && dotnet build Maple2.Server.World/Maple2.Server.World.csproj -c Debug

WORKDIR /app/Maple2.File.Ingest
ENTRYPOINT ["dotnet", "bin/Debug/net8.0/Maple2.File.Ingest.dll"]
