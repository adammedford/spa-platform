# The startup floor's image (#173). Owner: the owner OWNERSHIP.md names for tools/bench/.
#
# src/Gateway/Dockerfile's pattern, line for line where it applies: the same SDK and aspnet bases,
# named by the same digests beside the same tags, the same build files copied first, the same
# TARGETARCH restore and publish, the same runtime environment, the same port and the same non-root
# user. Only the project differs, and the gateway's OCI labels, which describe the image and not
# the process, are left out. A difference here is a difference in the floor, so change this file
# with the gateway's; tools/bench/startup_tests.py fails while the two files' FROM lines, or their
# final stages' ENV and USER lines, differ (#910).
#
# The bases are the gateway's for the gateway's reasons, which its Dockerfile gives above each FROM:
# pinned by digest so two builds of one commit build on the same layers (#781), the full Ubuntu SDK
# for the build stage, and the chiseled -extra runtime, which has no shell, for the final stage.
#
# Build from the repository root, as the gateway's is:
#   docker build -f tools/bench/floor/Dockerfile -t spa-bench/floor .

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ARG TARGETARCH
WORKDIR /src

COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig .globalconfig BannedSymbols.*.txt ./
COPY tools/bench/floor/Bench.Floor.csproj tools/bench/floor/
RUN dotnet restore tools/bench/floor/Bench.Floor.csproj -a "$TARGETARCH"

COPY tools/bench/floor/ tools/bench/floor/
RUN dotnet publish tools/bench/floor/Bench.Floor.csproj -a "$TARGETARCH" --no-restore -c Release -o /app

# The gateway's permission step, for the same reason: the final stage runs as $APP_UID (#204).
RUN chmod -R a+rX,go-w /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:9b755ea799ae649141a073e30d8b62d8095a5465af2b7694d17866e8c05131d8 AS final
WORKDIR /app

# The gateway's diagnostics switch: with it off the runtime opens no diagnostic port at start, so
# the floor starts the process the gateway's image starts.
ENV DOTNET_EnableDiagnostics=0

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "Bench.Floor.dll"]
