FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/ src/
# Restore from the committed lock files so the image uses the reviewed package graph.
# The NuGet cache mount keeps downloaded packages between builds.
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/OptiStorage.Server/OptiStorage.Server.csproj --locked-mode \
 && dotnet publish src/OptiStorage.Server/OptiStorage.Server.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
COPY docker/entrypoint.sh /usr/local/bin/optistorage-entrypoint
# The data root must be owned by the service account and closed to other users.
RUN chmod 0755 /usr/local/bin/optistorage-entrypoint \
 && mkdir -p /var/lib/optistorage/data \
 && chown -R app:app /var/lib/optistorage \
 && chmod 0700 /var/lib/optistorage /var/lib/optistorage/data
USER app
ENV OPTISTORAGE_DATA_ROOT=/var/lib/optistorage/data \
    OPTISTORAGE_DATA_URL=http://0.0.0.0:9000 \
    OPTISTORAGE_ADMIN_URL=http://127.0.0.1:9001
EXPOSE 9000
ENTRYPOINT ["/usr/local/bin/optistorage-entrypoint"]
