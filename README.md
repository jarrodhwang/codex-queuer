# Codex Queue

Codex Queue is a React + ASP.NET Core + SQLite web app for dispatching Codex CLI and headless OpenHands CLI work to local or SSH target machines. The web server records queue state, output, session history, project grouping, file browsing, and optional Codex commit sessions.

## Stack

- React 19 + TypeScript + Vite
- Ein UI style local components with the shadcn registry configured in `client/components.json`
- ASP.NET Core 10 Web API
- EF Core SQLite
- Apache HTTPD reverse proxy and SPA host
- Docker Compose on external port `6767`

## Local Development

Run the API:

```bash
dotnet run --project server/CodexQueue.Api --urls http://localhost:5153
```

Run the client:

```bash
cd client
npm install
npm run dev
```

Open `http://localhost:5173`.

## Docker

Copy the environment example and set a token before exposing the service:

```bash
cp .env.example .env
docker compose up --build
```

Open `http://localhost:6767`.

### Tailscale Serve path mounts

When using `tailscale serve --set-path=/codex`, set `VITE_BASE_PATH=/codex/`
in `.env` and rebuild the web image:

```bash
docker compose up --build -d
```

The generated UI then requests assets and the API through `/codex/`, which
Tailscale Serve forwards to the container. Keep `CQ_API_TOKEN` set: a Tailnet
limits network access, but this application can initiate Codex and SSH work.

The Docker default machine is `Local Linux`, an SSH target that connects from the API container to the host at `host.docker.internal`. This makes the default SSH project paths real host paths such as `/home/jarrod`, and runs the host machine's `codex`, `git`, and project dependencies instead of container-local binaries. The API still mounts `HOST_PROJECT_ROOT` at `CQ_CONTAINER_HOST_MOUNT_ROOT` for direct container-local browsing or fallback setups. If you create a `Local` container machine and leave its working root blank, it uses `CQ_DEFAULT_LOCAL_WORKING_ROOT`, which defaults to `/host/home/jarrod`. SSH keys are mounted from `HOST_SSH_DIR` to `/home/app/.ssh`; machine key paths should use the container path such as `/home/app/.ssh/zbook_fury`.

For Linux hosts, make sure SSH is running on the host and the key at `CQ_DEFAULT_SSH_KEY_PATH` is authorized for `CQ_DEFAULT_SSH_USER`. For Windows and macOS SSH targets, set the machine platform in the UI and use that machine's native working root, for example `C:\Users\you` or `/Users/you`.

For a macOS SSH machine, install the Codex CLI for the same account configured as the SSH user. A successful SSH login does not guarantee that `codex` is available: `sshd` normally starts a non-login shell with a reduced `PATH`. The machine check now reports the SSH connection separately from the CLI location and version. It searches the standard macOS Homebrew paths (`/opt/homebrew/bin` and `/usr/local/bin`) plus common per-user npm, Volta, asdf, nvm, Cargo, and pnpm paths. The setup also permits an empty nvm glob in zsh, so a machine without nvm reaches the actual Codex check. If the check reaches the Mac but reports that Codex is missing, log in as the configured user and run `npm install -g @openai/codex`, then rerun the check. Do not rely on a Codex desktop app installation alone unless its CLI command is available to that SSH user.

For a Windows SSH machine such as `192.168.0.50`, configure the machine as:

- Kind: `SSH`
- Platform: `Windows`
- Host: `192.168.0.50`
- User: `jarrod`
- SSH key: `/home/app/.ssh/zbook_fury`
- Working root: `C:\Users\jarrod`

Install Codex for the same Windows account configured as the SSH user. The Codex desktop app and Codex CLI are separate launch surfaces; verify the CLI from a fresh SSH connection with `ssh user@host codex --version`. Windows OpenSSH starts a non-interactive session, so Codex Queue restores the user and machine `PATH` and also checks the desktop app CLI directory plus the standard npm, Volta, Scoop, WinGet, Chocolatey, and Node locations. It supports the normal `codex.cmd` npm shim even when the SSH session does not provide `PATHEXT`. If the machine test reports that Codex is missing, sign in as that SSH user and run `npm.cmd install -g @openai/codex`, reconnect, and retry the machine test before queuing work. Windows PowerShell is forced to text output so SSH errors remain readable instead of appearing as CLIXML.

Codex runs over Windows SSH use `danger-full-access` because both native Windows sandbox modes can fail to initialize child processes in a non-interactive OpenSSH session with status `0xC0000142`. The queue still injects a strict project-root boundary into every prompt, but this is an instruction boundary rather than OS enforcement: the Codex process has the SSH user's filesystem and network access. Use a dedicated, least-privileged Windows account and restrict its NTFS permissions and SSH access. Local Windows runs continue to use the Codex-configured sandbox mode and desktop; Linux and macOS SSH runs retain `workspace-write` unless commit generation requires broader access.

## OpenHands Local Runner with Ollama

The first OpenHands slice keeps Codex Queue as the only browser UI and control plane. It runs the native OpenHands CLI headlessly on the selected Linux or macOS development machine, in that machine's selected repository. File access, Git, builds, tests, and terminal commands therefore stay on that machine. Conversation continuation is bound to the same queue tab, project, and machine; a request is never moved to another PC. The central Ollama server performs inference only and does not need repository mounts, SSH access, or a shell on any development machine. It receives only normal model requests, which can contain code and tool context selected by the agent.

### Install OpenHands on each target

Install OpenHands for the same OS account that Codex Queue uses locally or over SSH. The `uv` installation method requires Python 3.12 or newer:

```bash
uv tool install openhands --python 3.12
openhands --version
openhands --help
```

OpenHands also publishes a standalone executable installer for native Linux and macOS targets. The machine check reports OpenHands availability and version separately from the existing Codex check. Make sure `openhands` is also on the non-login SSH `PATH`; common per-user locations such as `~/.local/bin` may not be present automatically. See the official [OpenHands CLI installation guide](https://docs.openhands.dev/openhands/usage/cli/installation).

Native Windows OpenHands CLI is not supported. OpenHands requires [WSL on Windows](https://docs.openhands.dev/openhands/usage/cli/quick-start), and this first slice does not configure or claim support for a WSL bridge. Windows machines should report that requirement instead of attempting a native run.

### Configure the central Ollama server

Keep Ollama private to a trusted LAN or VPN and enforce that boundary with host firewall and network access controls. Do not expose port `11434` directly to the public internet. Configure the API-facing values in `.env`:

```dotenv
CQ_LOCAL_AI_BASE_URL=http://host-or-private-ip:11434/v1
CQ_LOCAL_AI_DEFAULT_MODEL=your-model:tag
CQ_LOCAL_AI_CONTEXT_WINDOW=65536
```

These are the app-facing configuration names, and the included Compose file passes them into the API container. They seed the initial `Local Ollama` profile; afterward the additive provider-profile API can update or add profiles without changing existing Codex data. Use a LAN/VPN address that is reachable both from Codex Queue and from every selected development machine. The base URL must use Ollama's OpenAI-compatible `/v1` endpoint. Codex Queue discovers installed models and their advertised context/tool/thinking capabilities from Ollama's [`/api/tags`](https://docs.ollama.com/api/tags) endpoint first and falls back to `/v1/models` when appropriate. Stored model identifiers remain `openai/<ollama-model-name>` for compatibility; OpenHands execution uses LiteLLM's native `ollama_chat/<ollama-model-name>` route so Ollama thinking and tool-call behavior are preserved. Codex Queue supplies the non-secret placeholder API key `local-llm` when Ollama has no authentication.

The Compose default, `http://host.docker.internal:11434/v1`, also supports the default SSH machine when it points back to the Docker host. Codex Queue keeps that address for API-container discovery and automatically uses `http://127.0.0.1:11434/v1` for the target-side check and OpenHands process, because Docker's special hostname normally does not resolve from the host itself. Other SSH machines continue to receive the configured profile address unchanged, so use a reachable private address for those machines.

Codex Queue requires 65,536 tokens for OpenHands project prompts. It sends that profile value to Ollama as the per-request `options.num_ctx` value, but the Ollama service should use the same default for other clients: set `OLLAMA_CONTEXT_LENGTH=65536`, restart Ollama, and verify the loaded allocation with `ollama ps`. A model whose advertised maximum is below 65,536 remains visible in the plain model list but is blocked for OpenHands because the server cannot allocate the required window. `/api/tags` reports the model's maximum, whereas `/api/ps` reports the currently loaded allocation. See the official [OpenHands local-LLM guide](https://docs.openhands.dev/openhands/usage/llms/local-llms), [Ollama context-length guide](https://docs.ollama.com/context-length), and [Ollama OpenAI compatibility documentation](https://docs.ollama.com/api/openai-compatibility).

GPT-OSS models expose Low, Medium, and High reasoning choices below the model picker, matching Ollama's selectable reasoning levels. Other thinking models remain in the same plain list without an effort selector because Ollama exposes thinking as an on/off capability for them. Codex Queue passes a supported choice as `LLM_REASONING_EFFORT`.

OpenHands CLI 1.16 does not natively read the context and reasoning overrides above. Codex Queue therefore writes a permission-restricted, per-run Python bootstrap next to the temporary task file; it applies the overrides in memory without modifying the installed OpenHands package and is deleted after the run. The OpenHands machine check verifies that the selected installation can load this integration. Per-run tmux sockets use a short random directory under `/tmp` to stay within Unix socket path limits even when the project path is long.

The UI and machine checks distinguish:

- OpenHands CLI missing or unavailable on the selected machine
- Ollama reachability from the Codex Queue server
- Ollama reachability from the selected development machine
- Selected model not installed or not visible from that machine
- Waiting for the shared Local AI slot

Local AI concurrency defaults to one globally across all connected PCs so one large central model is not overloaded. Health and model discovery are cached briefly, but execution still occurs on the selected machine.

### Headless permissions and execution safety

OpenHands is launched with JSONL output using the validated headless shape:

```text
openhands --headless --json --override-with-envs --always-approve -f <temporary-task-file>
```

As documented in [OpenHands headless mode](https://docs.openhands.dev/openhands/usage/cli/headless), headless OpenHands always auto-approves agent actions; this is not equivalent to any Codex sandbox or approval mode. `ReadOnly` and `AskForApproval` are unavailable, and `ApproveForMe` must not be silently converted to unrestricted access. A user must explicitly confirm the OpenHands unrestricted mode before `--always-approve` is allowed. The flag records that choice, although headless execution is inherently auto-approved. OpenHands then runs with the selected machine account's filesystem and network permissions, so use a dedicated least-privileged account and OS permissions to enforce boundaries outside the project.

Task prompts and provider values are kept out of process arguments and command previews. Temporary task and SSH environment files are restricted and removed after completion, failure, or cancellation. SSH execution removes unrelated exported variables before OpenHands starts, retaining only an explicit runtime allowlist. OpenHands JSON can include internal reasoning fields; Codex Queue exposes only user-visible messages, tool activity, commands, errors, summaries, and results. Oversized JSONL events and captured diagnostics are bounded before storage or browser streaming.

Every OpenHands task also receives explicit instructions not to leave the project, commit or push, rewrite history, elevate privileges, or inspect unrelated credentials. Those instructions are a safety boundary for the agent, not an OS sandbox; enforce the real boundary with a dedicated account, repository permissions, and network controls.

Attachments remain unchanged for Codex requests. They are disabled for OpenHands in this first slice because the existing transfer path runs before the OpenHands project-path guards; enable them only after local and SSH symbolic-link escape tests cover that workflow.

Cancellation terminates the launched process tree and releases the shared Local AI slot. OpenHands can create detached `tmux` sessions, and an interrupted SSH connection alone does not prove that a remote process stopped. After an abnormal SSH or host failure, check the target for an orphaned OpenHands/tmux session before retrying; hard power loss can also leave restricted temporary files for later cleanup. On API restart, interrupted OpenHands runs fail closed, and queued OpenHands work is paused as failed until a user verifies the targets and resumes it. Browser disconnects do not cancel server-side queue work, and stored output remains available after reconnecting.

This first slice intentionally supports unauthenticated Local Ollama with the non-secret `local-llm` placeholder only. Do not configure real OpenAI, Anthropic, or authenticated-Ollama credentials for OpenHands execution yet: current OpenHands CLI versions can pass `LLM_API_KEY` into agent-created child processes. Cloud and authenticated provider execution remain disabled until credential isolation can be validated.

The JSON event and persisted-conversation validation in this slice was tested against OpenHands CLI 1.16.0 with OpenHands SDK 1.21.0. The machine check verifies the required command flags; revalidate captured JSON events and conversation state before adopting a flag-compatible release with a different output or persistence schema.

## Queue Behavior

1. A request is queued against a project and model.
2. The worker processes each project's queue in order while running different project queues concurrently. Local/OpenHands requests also share the configured global provider limit, which defaults to one.
3. Codex requests keep the existing `codex exec --json` path on the project machine. Prompts are streamed over stdin instead of placed in process arguments, which supports long requests on Windows and avoids exposing prompt text in process listings.
4. Local requests run headless OpenHands on the same selected project machine and send inference requests to the configured central Ollama server.
5. Requests in the icon-only base tab keep the original behavior and start independent conversations. Named queue tabs retain separate Codex session IDs and OpenHands conversation IDs and continue each runner only on the same project and machine.
6. The browser terminal is a separate reusable shell session per project and machine. It preserves shell state while the terminal stays open, but it does not automatically attach queued `codex exec` jobs to that terminal chat history.
7. If Codex commit generation is enabled and the request succeeds, a second Codex session runs with the commit model. This option is disabled for OpenHands in the first slice.
8. Request and commit output are stored as separate runs and displayed together under the request details.

## Codex Session Model

- Queued work does not talk to the Codex desktop app session. It launches Codex CLI directly on the target machine.
- A request in the base queue starts a fresh Codex CLI thread. It does not inherit the browser terminal session or any Codex Desktop chat context.
- Named tabs persist their own Codex CLI thread ID and reuse it for later requests in that tab. Tabs are isolated by project.
- Separate commit session means exactly that: the follow-up commit prompt runs in a different Codex session and cannot see the earlier request chat unless you disable separate commit sessions.
- Deleting an inactive named tab retires its saved context and keeps its request records out of the base view. Tabs with active requests must be completed, cancelled, or cleared first.

## Security Notes

- Set `CQ_API_TOKEN` in `.env` for any non-private deployment.
- The app can trigger shell commands through Codex and SSH. Do not expose port `6767` without network controls and a strong token.
- SSH uses batch mode and key-based authentication only; passwords are not stored. The Docker host-local default requires the same SSH hardening as any other target machine.

## Practical Quality Notes

- Reliability: interrupted OpenHands runs are marked failed on API restart to avoid launching a duplicate unrestricted agent; queued OpenHands work pauses for explicit recovery, while existing Codex recovery/requeue behavior remains unchanged.
- Maintainability: HTTP routes, persistence, command execution, file browsing, and queue processing are separated.
- Performance: UI progress uses polling to keep the first version simple; switch to SignalR if many users or sub-second updates are needed.
- Portability: Docker Compose keeps Apache, API, and SQLite data isolated; target-specific Codex setup stays on each execution machine. SSH folder browsing uses portable shell commands for Linux/macOS targets and PowerShell for Windows targets.
