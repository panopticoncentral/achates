# Speech Setup (Kokoro-FastAPI)

Achates uses [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI) as
a local TTS sidecar. The model runs entirely on your machine; nothing is
sent to a cloud TTS vendor, and there is no content moderation.

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

## Test the sidecar standalone

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

If you hear the greeting, the sidecar is healthy.

## Tell Achates about it

In `~/.achates/config.yaml`:

```yaml
tools:
  speech:
    sidecar:
      working_dir: ~/kokoro-fastapi
      command: uv
      args: [run, uvicorn, api.src.main:app, --host, "127.0.0.1", --port, "8880"]
```

Restart Achates. On startup it spawns the sidecar (you'll see
`[kokoro]`-prefixed lines in the log) and marks speech available after
the health check passes — look for `Speech: Kokoro sidecar is ready.`

### External sidecar (alternative)

If you'd rather run the sidecar yourself (Docker, dev loop, a shared
instance), point Achates at it instead:

```yaml
tools:
  speech:
    endpoint: http://127.0.0.1:8880
```

Achates will not spawn a child process; it only health-checks the endpoint.

### Optional global default voice

```yaml
tools:
  speech:
    # ... sidecar or endpoint ...
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
  (`lsof -ti :8880 | xargs kill`) or change `--port` in both the sidecar
  args and the Achates config.
- **No audio events in the app** — check the per-session toggle is on
  (speaker icon in the nav bar) and the agent has `**Voice:**` set (or
  `tools.speech.default_voice` is configured globally).
- **`Speech unavailable` chip on every message** — check the Achates
  server logs for `[kokoro]`-prefixed errors; verify the sidecar process
  is running with `lsof -i :8880`.

## Uninstall

```bash
rm -rf ~/kokoro-fastapi /tmp/achates-speech-*.mp3
```

Remove the `tools.speech` block from `~/.achates/config.yaml`.
