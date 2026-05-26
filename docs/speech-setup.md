# Speech Setup (Kokoro-FastAPI)

Achates uses [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI) as
a local TTS sidecar. The model runs entirely on your machine; nothing is
sent to a cloud TTS vendor, and there is no content moderation.

The sidecar is managed externally — you start it yourself however you
prefer (a terminal, launchd, systemd, Docker, …) and point Achates at it
via `tools.speech.endpoint`. Achates only sends HTTP requests; it does
not spawn or supervise the process.

This guide walks through a one-time install on macOS (Apple Silicon).
Linux is similar; Windows is not currently tested.

## Prerequisites

```bash
brew install ffmpeg uv jq
```

## Install Kokoro-FastAPI

```bash
git clone https://github.com/remsky/Kokoro-FastAPI.git ~/kokoro-fastapi
cd ~/kokoro-fastapi
uv sync
bash docker/scripts/download_model.sh   # downloads kokoro-v1_0.pth (~330MB)
```

## Configure for native (non-Docker) run

The default config in the repo is wired for the Docker container layout
(`/app/...` paths, CUDA). Override for native run:

```bash
cat > ~/kokoro-fastapi/.env <<'EOF'
USE_GPU=false
MODEL_DIR=/Users/<you>/kokoro-fastapi/api/src/models
VOICES_DIR=/Users/<you>/kokoro-fastapi/api/src/voices/v1_0
EOF
```

Replace `<you>` with your username. (The `.env` is loaded by uvicorn from
the working directory.)

## Run the sidecar

```bash
cd ~/kokoro-fastapi
uv run uvicorn api.src.main:app --host 127.0.0.1 --port 8880
```

You should see `Initializing Kokoro V1 on cpu` and `Application startup
complete`. From another terminal:

```bash
curl -X POST http://127.0.0.1:8880/v1/audio/speech \
  -H "Content-Type: application/json" \
  -d '{"model":"kokoro","voice":"af_nicole","input":"Hello.","response_format":"mp3"}' \
  --output /tmp/test.mp3 && afplay /tmp/test.mp3
```

If you hear the greeting, the sidecar is healthy. Leave it running while
you use Achates — or wrap it in `launchd` / `systemd` / a Docker
container so it starts at login.

## Tell Achates about it

In `~/.achates/config.yaml`:

```yaml
tools:
  speech:
    endpoint: http://127.0.0.1:8880
```

The `endpoint` field is optional — it defaults to `http://127.0.0.1:8880`
(Kokoro-FastAPI's own default), so an empty `speech:` block is enough if
you're running the sidecar locally on the default port. Restart Achates
after editing the config.

### Optional global default voice

```yaml
tools:
  speech:
    endpoint: http://127.0.0.1:8880
    default_voice: af_nicole
```

Used when an agent doesn't declare `**Voice:**`. Off by default —
voiceless agents stay silent.

## Per-agent voice

Add a `**Voice:**` capability to any agent's `AGENT.md`:

```markdown
## Capabilities

**Voice:** af_nicole
```

Or a blend:

```markdown
**Voice:** af_nicole(0.7)+af_bella(0.3)
```

The iOS app's agent edit sheet exposes the same field via a picker plus
a custom-blend text field.

## Enable speech for a session

In the iOS app, tap the speaker icon in the chat nav bar to toggle it
on. Replies will be spoken as they stream.

## Troubleshooting

- **`Read-only file system: /app`** — `MODEL_DIR`/`VOICES_DIR` in `.env`
  point at container paths. Set them to absolute paths under your home.
- **`Initializing Kokoro V1 on cuda`** — `USE_GPU=true` (default).
  Set `USE_GPU=false` in `.env`.
- **Port 8880 in use** — kill the existing listener
  (`lsof -ti :8880 | xargs kill`) or change `--port` for the sidecar and
  set the same value in `tools.speech.endpoint`.
- **`audio.error` chip on the first sentence of every reply** — the
  endpoint is unreachable. Verify the sidecar is running
  (`lsof -i :8880`) and that the URL in `tools.speech.endpoint` matches.
  Achates only attempts the first sentence per turn after a failure —
  subsequent sentences in the same turn are skipped silently.
- **No audio events at all** — check the per-session toggle is on
  (speaker icon in the nav bar) and the agent has `**Voice:**` set (or
  `tools.speech.default_voice` is configured globally).

## Uninstall

```bash
rm -rf ~/kokoro-fastapi /tmp/achates-speech-*.mp3
```

Remove the `tools.speech` block from `~/.achates/config.yaml`.
