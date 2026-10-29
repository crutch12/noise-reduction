# окружение сборки и тестов для Linux, на Mac запускается в Docker
# Debian 11 с glibc 2.31: собранное запускается на Ubuntu 20.04+, Debian 11+ и новее
FROM rust:1.98-bullseye
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends unzip \
    && apt-get clean
WORKDIR /src
