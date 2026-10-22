# The mock API image. Owner: workstream 5, task 5.3.
#
# ARM64, like the gateway, because both containers run in the same ARM64 task (task 1.4).
# deploy.sh builds and pushes by digest, so the tag here never decides what runs (task 1.7).
#
# Build from the repository root, because the build needs Directory.Packages.props:
#   docker build -f fixtures/MockApi/Dockerfile -t mock-api .

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /src

# The build files first, so a source change does not re-resolve every package.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY fixtures/MockApi/MockApi.csproj fixtures/MockApi/

# No Platform.* project is copied, because none is referenced (check A-02). The mock API's job is to
# show that an upstream validates the bearer token itself, and one sharing the gateway's code would
# prove nothing.
RUN dotnet restore fixtures/MockApi/MockApi.csproj -a "$TARGETARCH"

COPY fixtures/MockApi/ fixtures/MockApi/
RUN dotnet publish fixtures/MockApi/MockApi.csproj -a "$TARGETARCH" --no-restore -c Release -o /app

# The final stage runs as $APP_UID, and COPY and publish keep the modes the files had in the build
# context, so an owner-only file on a workstation reaches the image owner-only and the runtime user
# cannot read it: the gateway died that way in WebApplication.CreateBuilder (#204). Everyone may read
# every file and enter every directory, executables keep their bit, and no one but root may write.
# The step is src/Gateway/Dockerfile's own, character for character (#233). The copy into the final
# stage is owned by root, as COPY makes it without --chown.
# https://docs.docker.com/reference/dockerfile/#copy---chown---chmod
RUN chmod -R a+rX,go-w /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app

# Two listeners, one per API, on the loopback ports the app definitions in fixtures/apps/ name as
# their upstreams. They are reached from the gateway container over loopback inside the task, never
# from outside it, so neither is published to the network.
EXPOSE 5080 5081

# The ports are the application's own, not Kestrel's defaults, so ASPNETCORE_HTTP_PORTS is not set:
# MockApiHost binds each listener itself.
#
# These three have no default and the process refuses to start without them. The client ids come
# from the apps root's state, which is where Terraform put them (task 1.4).
ENV MockApi__Issuer=""
ENV MockApi__App1Client=""
ENV MockApi__App2Client=""

# The image runs as the non-root user the base image ships.
USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "MockApi.dll"]
