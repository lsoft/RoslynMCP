FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["RoslynMcpServer.csproj", "."]
RUN dotnet restore "RoslynMcpServer.csproj"

# Copy source code and publish
COPY . .
RUN dotnet publish "RoslynMcpServer.csproj" -c Release -o /app/publish --no-restore --self-contained false

# The runtime image is not enough: MSBuildWorkspace evaluates the analyzed .sln/.csproj files with
# an installed .NET SDK. The SDK also bounds which projects can be analyzed: this image handles
# net10.0 and older; projects targeting a newer .NET need a matching SDK image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS final
WORKDIR /app

# stdout carries the MCP protocol, keep the SDK's first-run banners out of it
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

COPY --from=build /app/publish .

# Create directory for workspace mounting
RUN mkdir -p /workspace

ENTRYPOINT ["dotnet", "RoslynMcpServer.dll"]
