# The gateway image. Owner: integrator, written once at task I-4.
#
# ARM64, because the ECS task is ARM64 with 0.25 vCPU and 1 GB (task 1.4). deploy.sh builds and
# pushes by digest and then asserts the running digest, so the tag here never decides what runs
# (task 1.7).
#
# Build from the repository root, because the build needs Directory.Packages.props and the
# workstream projects the gateway references:
#   docker build -f src/Gateway/Dockerfile -t gateway .

# Both FROM lines name their base by digest beside its tag, so two builds of one commit build on the
# same layers, whoever builds them and whenever (#781). The digest is the tag's manifest list, which
# names an image for each architecture, so the ARM64 deployment and an x64 workstation resolve the
# same pin. The tag stays beside it for the reader and for the pins sweep, which reports both digests
# (tools/pins_inventory.py); moving to a new patch is a commit that changes the digest. Read a tag's
# current digest with `docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0-noble`.
#
# The build stage stays on the full Ubuntu SDK: Microsoft produces no SDK image of the chiseled type
# (https://learn.microsoft.com/en-us/dotnet/core/docker/container-images#images-optimized-for-size),
# and nothing of this stage reaches the final image but /app.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ARG TARGETARCH
WORKDIR /src

# The build files first, so a source change does not re-resolve every package. The two analyzer
# configs come with them, so the image publishes under the analysis every other build runs
# (ADR-0035); Directory.Build.props switches analysis on only where .globalconfig is (#606), and
# attaches the banned-symbol lists there too, so they come along (#608).
COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig .globalconfig BannedSymbols.*.txt ./
COPY src/Gateway/Gateway.csproj src/Gateway/
COPY src/Platform.Abstractions/Platform.Abstractions.csproj src/Platform.Abstractions/
COPY src/Platform.Hosting/Platform.Hosting.csproj src/Platform.Hosting/
COPY src/Platform.Identity/Platform.Identity.csproj src/Platform.Identity/
COPY src/Platform.Proxy/Platform.Proxy.csproj src/Platform.Proxy/
COPY src/Platform.Registry/Platform.Registry.csproj src/Platform.Registry/
COPY src/Platform.Security/Platform.Security.csproj src/Platform.Security/
COPY src/Platform.Telemetry/Platform.Telemetry.csproj src/Platform.Telemetry/

# Platform.Testing is not copied and not referenced: the gateway never ships a test double.
RUN dotnet restore src/Gateway/Gateway.csproj -a "$TARGETARCH"

# The contract schemas, for Platform.Registry to link and embed. It embeds its own copies under
# Schemas/ today, so nothing reads these yet: the line lands first so that the workstream 2 follow-up
# which replaces the copies with a link to ../../contracts can be one owner's push (change request
# 2.1, decided 14 October 2026, a prerequisite of task I-6). After the restore, so a schema change
# does not re-resolve every package.
COPY contracts/*.schema.json contracts/

COPY src/ src/
RUN dotnet publish src/Gateway/Gateway.csproj -a "$TARGETARCH" --no-restore -c Release -o /app

# The final stage runs as $APP_UID, and COPY and publish keep the modes the files had in the build
# context, so an owner-only appsettings.json on a workstation reached the image owner-only and the
# gateway died in WebApplication.CreateBuilder with UnauthorizedAccessException (#204). Everyone may
# read every file and enter every directory, executables keep their bit, and no one but root may
# write. The copy into the final stage is owned by root, as COPY makes it without --chown.
# https://docs.docker.com/reference/dockerfile/#copy---chown---chmod
RUN chmod -R a+rX,go-w /app

# The runtime stage is Ubuntu chiseled: distroless, with no shell and no package manager, and a
# non-root user by default (https://github.com/dotnet/dotnet-docker/blob/main/documentation/ubuntu-chiseled.md).
# The -extra variant, because the plain chiseled images carry no ICU or tzdata and work only for an
# app in globalization invariant mode, and nothing in the solution sets InvariantGlobalization
# (https://learn.microsoft.com/en-us/dotnet/core/docker/container-images#scenario-based-images).
# Pinned by digest for the reason above the build stage's FROM. tools/image-check.sh fails an image
# that has /bin/sh.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:9b755ea799ae649141a073e30d8b62d8095a5465af2b7694d17866e8c05131d8 AS final
WORKDIR /app

# Off: the runtime's debugger, profiler and EventPipe diagnostics, and with them the diagnostic port
# a process in the task could attach to
# (https://learn.microsoft.com/en-us/dotnet/core/runtime-config/debugging-profiling#enable-diagnostics).
# So dotnet-trace, dotnet-counters and dotnet-dump cannot attach to this image's gateway; reading one
# needs a debugging image built without this line. tools/image-check.sh fails an image without it.
ENV DOTNET_EnableDiagnostics=0

# What built the image, as the OCI annotations name it
# (https://github.com/opencontainers/image-spec/blob/main/annotations.md#pre-defined-annotation-keys).
# The build passes each with --build-arg; one it does not pass reads "unset", so a missing value says
# so rather than reading as an empty string. tools/image-check.sh fails an image without one of them.
ARG SOURCE=unset
ARG REVISION=unset
ARG VERSION=unset
LABEL org.opencontainers.image.source="$SOURCE"
LABEL org.opencontainers.image.revision="$REVISION"
LABEL org.opencontainers.image.version="$VERSION"

# 8080, which is what the ALB target group sends to (task 1.3). No TLS in the container: the
# listener terminates it.
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

# The image runs as the non-root user the base image ships, by its number, as the chiseled base's
# own user is.
USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "Gateway.dll"]
