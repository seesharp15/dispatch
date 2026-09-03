# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build the .NET app
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project file alone so the layer caches across source edits.
COPY src/Dispatch.Web/Dispatch.Web.csproj src/Dispatch.Web/
RUN dotnet restore src/Dispatch.Web/Dispatch.Web.csproj

COPY src/ src/
RUN dotnet publish src/Dispatch.Web/Dispatch.Web.csproj \
      -c Release \
      -o /app/publish \
      --no-restore \
      /p:UseAppHost=false

# ---------------------------------------------------------------------------
# Runtime: the app plus the two binaries it shells out to (ffmpeg, whisper)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

ENV DEBIAN_FRONTEND=noninteractive \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ffmpeg \
      python3 \
      python3-pip \
      python3-venv \
      ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# openai-whisper pulls torch, which defaults to the multi-gigabyte CUDA build.
# Render web services have no GPU, so pin the CPU-only wheel index and keep the
# image (and the cold-start download) roughly a third of the size.
RUN python3 -m venv /opt/whisper \
 && /opt/whisper/bin/pip install --upgrade pip \
 && /opt/whisper/bin/pip install \
      --extra-index-url https://download.pytorch.org/whl/cpu \
      openai-whisper

# Bake the model into the image. Without this the first transcription of every
# fresh container stalls on a ~150 MB download from OpenAI's CDN, and fails
# outright if that CDN is unreachable.
ARG WHISPER_MODEL=base.en
ENV XDG_CACHE_HOME=/opt/whisper-cache
RUN /opt/whisper/bin/python -c "import whisper; whisper.load_model('${WHISPER_MODEL}')"

WORKDIR /app
COPY --from=build /app/publish ./

# Storage defaults point at the mounted disk; see render.yaml.
ENV ASPNETCORE_ENVIRONMENT=Production \
    Decoder__FfmpegPath=/usr/bin/ffmpeg \
    Transcription__WhisperCliPath=/opt/whisper/bin/whisper \
    Transcription__WhisperModel=${WHISPER_MODEL} \
    Storage__RootPath=/var/data \
    Storage__DatabasePath=/var/data/dispatch.db \
    Storage__RecordingsPath=/var/data/recordings \
    Storage__DataProtectionKeysPath=/var/data/keys \
    ForwardedHeaders__TrustProxy=true

EXPOSE 10000

ENTRYPOINT ["dotnet", "Dispatch.Web.dll"]
