# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY IndexInvestment.sln .
COPY src/IndexInvestment.Core src/IndexInvestment.Core
COPY src/IndexInvestment.Web src/IndexInvestment.Web
RUN dotnet publish src/IndexInvestment.Web -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Binds to all interfaces inside the container; nginx is the only
# external entry point (see nginx.conf).
EXPOSE 8000
ENTRYPOINT ["dotnet", "IndexInvestment.Web.dll"]
