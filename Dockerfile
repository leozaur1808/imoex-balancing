# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY IndexInvestment.sln .
COPY src/IndexInvestment.Core src/IndexInvestment.Core
COPY src/IndexInvestment.Web src/IndexInvestment.Web
RUN dotnet publish src/IndexInvestment.Web -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Run as an unprivileged user so files written into the bind-mounted
# data/ (SQLite DB, incl. password hashes) are not root-owned on the host.
# On Docker Desktop for macOS this just works; on a Linux host, chown
# data/ to uid 1000 first.
RUN groupadd --gid 1000 app \
    && useradd --uid 1000 --gid app --create-home app \
    && mkdir -p /app/data \
    && chown -R app:app /app
USER app

COPY --from=build /app/publish .

# Binds to all interfaces inside the container; nginx is the only
# external entry point (see nginx.conf).
EXPOSE 8000
ENTRYPOINT ["dotnet", "IndexInvestment.Web.dll"]
