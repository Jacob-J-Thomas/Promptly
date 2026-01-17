# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files
COPY ["Promptly.Server/Promptly.Server.csproj", "Promptly.Server/"]
COPY ["Promptly.Application/Promptly.Application.csproj", "Promptly.Application/"]
COPY ["Promptly.Infrastructure/Promptly.Infrastructure.csproj", "Promptly.Infrastructure/"]
COPY ["Promptly.Domain/Promptly.Domain.csproj", "Promptly.Domain/"]

# Restore dependencies
RUN dotnet restore "Promptly.Server/Promptly.Server.csproj"

# Copy everything and build
COPY . .
WORKDIR "/src/Promptly.Server"
RUN dotnet build "Promptly.Server.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "Promptly.Server.csproj" -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5000

COPY --from=publish /app/publish .

# Migrations will be handled by the application at startup
ENTRYPOINT ["dotnet", "Promptly.Server.dll"]