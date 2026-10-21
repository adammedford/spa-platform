# The startup floor's image (#173). Owner: the owner OWNERSHIP.md names for tools/bench/.
#
# src/Gateway/Dockerfile's pattern, line for line where it applies: the same SDK and aspnet bases,
# the same build files copied first, the same TARGETARCH restore and publish, the same port and the
# same non-root user. Only the project differs. A difference here is a difference in the floor, so
# change this file with the gateway's.
#
# Build from the repository root, as the gateway's is:
#   docker build -f tools/bench/floor/Dockerfile -t spa-bench/floor .

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /src

COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY tools/bench/floor/Bench.Floor.csproj tools/bench/floor/
RUN dotnet restore tools/bench/floor/Bench.Floor.csproj -a "$TARGETARCH"

COPY tools/bench/floor/ tools/bench/floor/
RUN dotnet publish tools/bench/floor/Bench.Floor.csproj -a "$TARGETARCH" --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "Bench.Floor.dll"]
