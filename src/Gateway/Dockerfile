# The gateway image. Owner: integrator, written once at task I-4.
#
# ARM64, because the ECS task is ARM64 with 0.25 vCPU and 1 GB (task 1.4). deploy.sh builds and
# pushes by digest and then asserts the running digest, so the tag here never decides what runs
# (task 1.7).
#
# Build from the repository root, because the build needs Directory.Packages.props and the
# workstream projects the gateway references:
#   docker build -f src/Gateway/Dockerfile -t gateway .

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /src

# The build files first, so a source change does not re-resolve every package.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app

# 8080, which is what the ALB target group sends to (task 1.3). No TLS in the container: the
# listener terminates it.
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

# The image runs as the non-root user the base image ships.
USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "Gateway.dll"]
