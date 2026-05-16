# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

ARG GIT_COMMIT=unknown
ARG GIT_AUTHOR=unknown
ARG GIT_BRANCH=unknown
ARG GIT_MESSAGE=unknown
ARG BUILD_TIME=unknown

COPY *.csproj .
RUN dotnet restore

COPY . .

RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ARG GIT_COMMIT=unknown
ARG GIT_AUTHOR=unknown
ARG GIT_BRANCH=unknown
ARG GIT_MESSAGE=unknown
ARG BUILD_TIME=unknown

ENV GIT_COMMIT=$GIT_COMMIT
ENV GIT_AUTHOR=$GIT_AUTHOR
ENV GIT_BRANCH=$GIT_BRANCH
ENV GIT_MESSAGE=$GIT_MESSAGE
ENV BUILD_TIME=$BUILD_TIME

RUN apt-get update && apt-get install -y --no-install-recommends \
    chromium \
    ca-certificates \
    fonts-liberation \
    fonts-dejavu \
    fonts-noto \
    fontconfig \
    && fc-cache -f \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

ENV PUPPETEER_EXECUTABLE_PATH=/usr/bin/chromium
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .

RUN useradd -m appuser && chown -R appuser /app
USER appuser

EXPOSE 8080
ENTRYPOINT ["dotnet", "FileConverter.dll"]
