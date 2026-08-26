# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Raft persists its write-ahead log, snapshots, and member list to disk. /app is root-owned, so a
# non-root process cannot create a state directory there. Give the app a directory it owns.
#
# VOLUME keeps that state off the container's writable layer, but it does NOT by itself survive
# the container being replaced. To keep state across container replacement, mount a named volume
# over this path:
#     docker run -v erl-state:/var/lib/elasticratelimiter ...

ENV Raft__StateDir=/var/lib/elasticratelimiter
RUN mkdir -p /var/lib/elasticratelimiter && chown -R $APP_UID /var/lib/elasticratelimiter
VOLUME /var/lib/elasticratelimiter

# The address this node advertises to its peers. The default matches the container's own HTTP
# port so a single-node `docker run` works unattended. A multi-node cluster MUST override this
# per node with an address the other nodes can actually reach, and set Raft__ColdStart=false on
# every node except the one that bootstraps the cluster.
ENV Raft__PublicEndPoint=http://localhost:8080
ENV Raft__ColdStart=true

USER $APP_UID


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["ElasticRateLimiter.Server/ElasticRateLimiter.Server.csproj", "ElasticRateLimiter.Server/"]
RUN dotnet restore "./ElasticRateLimiter.Server/ElasticRateLimiter.Server.csproj"
COPY . .
WORKDIR "/src/ElasticRateLimiter.Server"
RUN dotnet build "./ElasticRateLimiter.Server.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./ElasticRateLimiter.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ElasticRateLimiter.Server.dll"]