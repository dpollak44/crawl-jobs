# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig Crawler.sln ./
COPY src/Crawler.Domain/Crawler.Domain.csproj src/Crawler.Domain/
COPY src/Crawler.Api/Crawler.Api.csproj src/Crawler.Api/
RUN dotnet restore src/Crawler.Api/Crawler.Api.csproj
COPY src/ src/
RUN dotnet publish src/Crawler.Api/Crawler.Api.csproj -c Release -o /app --no-restore

# Run
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENV ConnectionStrings__Default="Data Source=/data/crawler.db"
RUN mkdir -p /data && chown "$APP_UID" /data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Crawler.Api.dll"]
