# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY *.csproj .
RUN dotnet restore

COPY . .

RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Layer nặng (Chromium + font) đứng trước mọi thứ đổi theo từng bản build để được cache: deploy chỉ tải lại phần app
RUN apt-get update && apt-get install -y --no-install-recommends \
    chromium \
    ca-certificates \
    fonts-liberation2 \
    fonts-crosextra-carlito \
    fonts-noto-cjk \
    fonts-dejavu \
    fonts-noto \
    fontconfig \
    && fc-cache -f \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

RUN useradd -m appuser

ENV PUPPETEER_EXECUTABLE_PATH=/usr/bin/chromium
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build --chown=appuser /app/publish .

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

USER appuser

EXPOSE 8080
ENTRYPOINT ["dotnet", "FileConverter.dll"]
