# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

COPY src/*.csproj ./src/
RUN dotnet restore src/PstWeb.csproj

COPY src/ ./src/
RUN dotnet publish src/PstWeb.csproj -c Release -o /out --no-restore

# ── Stage 2: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# wkhtmltox shared library (libwkhtmltox.so) required by DinkToPdf
# The Debian wkhtmltopdf package only ships the CLI binary, not the .so;
# the GitHub release .deb includes both.
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgdiplus \
    libc6-dev \
    xvfb \
    wget \
    openssl \
    fontconfig \
    libxrender1 \
    libxext6 \
    libfreetype6 \
    libjpeg62-turbo \
    libpng16-16 \
    && wget -q https://github.com/wkhtmltopdf/packaging/releases/download/0.12.6.1-3/wkhtmltox_0.12.6.1-3.bookworm_amd64.deb -O /tmp/wkhtmltox.deb \
    && apt-get install -y --no-install-recommends /tmp/wkhtmltox.deb \
    && rm /tmp/wkhtmltox.deb \
    && rm -rf /var/lib/apt/lists/*

# Ensure libwkhtmltox.so is on the linker path
RUN ldconfig

# wkhtmltopdf on headless Linux needs a virtual display; wrap it
RUN printf '#!/bin/bash\nxvfb-run -a --server-args="-screen 0 1024x768x24" /usr/local/bin/wkhtmltopdf "$@"' \
    > /usr/local/bin/wkhtmltopdf-headless && \
    chmod +x /usr/local/bin/wkhtmltopdf-headless

WORKDIR /app
COPY --from=build /out ./

# Generate self-signed TLS certificate for HTTPS (baked into the image)
RUN openssl req -x509 -newkey rsa:4096 -sha256 -days 3650 -nodes \
    -keyout /app/tls.key -out /app/tls.crt \
    -subj "/CN=localhost/O=PST Explorer" \
    -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"

# Ensure data directories exist
RUN mkdir -p /data/pst /data/exports

EXPOSE 8080 8443
ENTRYPOINT ["dotnet", "PstWeb.dll"]
