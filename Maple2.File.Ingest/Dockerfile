FROM mcr.microsoft.com/dotnet/sdk:8.0

WORKDIR /app
COPY [".config/dotnet-tools.json", ".config/dotnet-tools.json"]
RUN dotnet tool restore

WORKDIR /app/Maple2.File.Ingest
ENTRYPOINT ["dotnet", "run"]
