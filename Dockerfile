# One recipe for both .NET services; PROJECT selects which one. Multi-stage: SDK to build, slim runtime to run.
ARG PROJECT=Crawler.Api

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG PROJECT
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig Crawler.sln ./
COPY src/Crawler.Domain/Crawler.Domain.csproj src/Crawler.Domain/
COPY src/Crawler.Infrastructure/Crawler.Infrastructure.csproj src/Crawler.Infrastructure/
COPY src/Crawler.Api/Crawler.Api.csproj src/Crawler.Api/
COPY src/Crawler.Worker/Crawler.Worker.csproj src/Crawler.Worker/
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj
COPY src/Crawler.Domain/ src/Crawler.Domain/
COPY src/Crawler.Infrastructure/ src/Crawler.Infrastructure/
COPY src/Crawler.Api/ src/Crawler.Api/
COPY src/Crawler.Worker/ src/Crawler.Worker/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
ARG PROJECT
WORKDIR /app
COPY --from=build /app .
RUN apt-get update && apt-get install -y --no-install-recommends wget && rm -rf /var/lib/apt/lists/*
USER $APP_UID
ENV PROJECT_DLL=${PROJECT}.dll
ENTRYPOINT ["sh", "-c", "dotnet $PROJECT_DLL"]
