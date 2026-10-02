FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/ src/
# Restore from the committed lock files so the image uses the reviewed package graph.
# The NuGet cache mount keeps downloaded packages between builds.
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/XStorage.Server/XStorage.Server.csproj --locked-mode \
 && dotnet publish src/XStorage.Server/XStorage.Server.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
COPY docker/entrypoint.sh /usr/local/bin/xstorage-entrypoint
# The data root must be owned by the service account and closed to other users.
RUN chmod 0755 /usr/local/bin/xstorage-entrypoint \
 && mkdir -p /var/lib/xstorage/data \
 && chown -R app:app /var/lib/xstorage \
 && chmod 0700 /var/lib/xstorage /var/lib/xstorage/data
USER app
ENV XSTORAGE_DATA_ROOT=/var/lib/xstorage/data \
    XSTORAGE_DATA_URL=http://0.0.0.0:9000 \
    XSTORAGE_ADMIN_URL=http://127.0.0.1:9001
EXPOSE 9000
ENTRYPOINT ["/usr/local/bin/xstorage-entrypoint"]
