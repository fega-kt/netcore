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

RUN printf 'GIT_COMMIT=%s\nGIT_AUTHOR=%s\nGIT_BRANCH=%s\nGIT_MESSAGE=%s\nBUILD_TIME=%s\n' \
    "$GIT_COMMIT" "$GIT_AUTHOR" "$GIT_BRANCH" "$GIT_MESSAGE" "$BUILD_TIME" > .env

RUN dotnet publish -c Release -o /app/publish --no-restore \
    && cp .env /app/publish/.env

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

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
