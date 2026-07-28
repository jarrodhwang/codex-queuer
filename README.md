# Codex Queue

Codex Queue is a React + ASP.NET Core + SQLite web app for dispatching Codex CLI work to local or SSH target machines. Standard Codex requests use OpenAI-hosted models, while Local requests use the same Codex CLI with an external OpenAI-compatible AI server. The web server records queue state, output, session history, project grouping, file browsing, and optional Codex commit sessions.

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

## Local Codex with an external AI server

The Local runner now launches the same native Codex CLI as the standard runner, on the selected local or SSH development machine and inside the selected repository. The standard Codex path is unchanged and continues to use its existing OpenAI model, authentication, sandbox, session, attachment, and commit behavior. Local requests instead add a per-invocation custom model provider that points Codex at the selected external AI server.

Install Codex CLI for the same OS account that Codex Queue uses on every target:

```bash
npm install -g @openai/codex
codex --version
```

For SSH targets, verify `codex --version` from a fresh non-login SSH connection. The existing machine setup notes above cover the platform-specific `PATH` handling for Linux, macOS, and Windows.

### AI server profiles

Create or edit a Local AI Server profile in Settings and select its server type:

| Server type | Typical base URL | Required API |
| --- | --- | --- |
| Ollama | `http://server:11434/v1` | OpenAI-compatible Models and Responses APIs |
| LM Studio | `http://server:1234/v1` | OpenAI-compatible Models and Responses APIs |
| llama.cpp | `http://server:8080/v1` | OpenAI-compatible Models and Responses APIs |

The address may point to another PC. It must be reachable from both the Codex Queue API and every target machine that will run Codex. Keep unauthenticated servers on a trusted LAN or VPN, apply host firewall rules, and do not expose them directly to the public internet. This release deliberately rejects authenticated Local profiles so credentials cannot be inherited by agent-launched commands.

The backend must implement both `GET /v1/models` and `POST /v1/responses`; Chat Completions compatibility alone is not sufficient for Codex. Use Ollama 0.13.3 or newer, LM Studio 0.3.29 or newer, or a recent llama.cpp server build with Responses API support. See the official [Codex custom-provider configuration](https://learn.chatgpt.com/docs/config-file/config-advanced#custom-model-providers), [Ollama OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility), [LM Studio Responses API](https://lmstudio.ai/docs/developer/openai-compat/responses), and [llama.cpp server documentation](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md).

Ollama profiles discover models from `/api/tags` and use any capability metadata that endpoint exposes; LM Studio and llama.cpp profiles use `/v1/models`. The readiness action checks model discovery and target-side `/v1/models` access without spending inference capacity on a sample generation. Because model-list metadata is not a reliable proof of tool support across servers, Responses and tool compatibility are authoritatively checked when the queued Codex task starts. Model identifiers are stored and sent exactly as advertised by the server. Older saved Local values with a synthetic `openai/` prefix remain readable when the exact raw model is available, but new selections are never rewritten.

The Compose environment values seed the initial Ollama profile:

```dotenv
CQ_LOCAL_AI_BASE_URL=http://host-or-private-ip:11434/v1
CQ_LOCAL_AI_DEFAULT_MODEL=your-model:tag
CQ_LOCAL_AI_CONTEXT_WINDOW=131072
```

Additional Ollama, LM Studio, and llama.cpp profiles are created in the UI. The default `http://host.docker.internal:11434/v1` address works when both the API container and its default SSH target refer to the Docker host. For that exact host pairing, Codex Queue changes the target-side address to `http://127.0.0.1:11434/v1`; other remote targets receive the configured private address unchanged.

### Context, reasoning, and sessions

Local Codex passes the selected raw model ID, `model_reasoning_effort`, and `model_context_window` through Codex's custom-provider configuration. Low, Medium, and High reasoning choices are offered when the discovered model advertises selectable reasoning support. Provider configuration is repeated for `codex exec resume`, preventing a Local session from falling back to the standard OpenAI provider.

`model_context_window` controls Codex's token accounting and compaction threshold; it does not allocate memory in Ollama, LM Studio, or llama.cpp. Configure the backend with an equal or larger context window. Codex Queue requires at least 32,768 tokens for its Local workflow and validates the chosen value against the profile and any advertised model maximum. Larger windows consume more accelerator memory and may reduce throughput, so use the smallest value that reliably fits the project.

This release offers request presets through 262,144 tokens. Current Codex releases apply conservative fallback metadata to unknown model IDs—including typical local-server model names—and silently clamp larger configured values, so 512K and 1M choices would not work as displayed for those models. A Local AI server profile may still record its larger backend allocation; dynamically exposing larger Codex windows for recognized or custom-catalog models is left for a later increment.

Named queue tabs persist separate standard Codex and Local Codex session IDs. A Local session stays bound to its original project and machine and resumes with the same provider settings. The base tab continues to start independent sessions.

The UI and readiness checks distinguish:

- Codex CLI missing or unavailable on the selected target
- AI server reachability from Codex Queue
- `/v1/models` reachability from the selected target
- selected model missing from the target-visible catalog
- waiting for the shared Local AI concurrency slot

Local AI concurrency defaults to one globally across all connected PCs so one large inference server is not overloaded. Model discovery is cached briefly, while every execution still performs server-side validation and a target-side route check.

### Permissions and execution safety

The Local UI currently requires Full access with explicit confirmation. It also disables attachments and automatic commit generation until those workflows have dedicated Local path-boundary coverage. Standard Codex requests retain their existing choices and are not affected by these restrictions.

Prompts are streamed over stdin rather than exposed in process arguments. Local Codex child processes receive an explicit runtime-variable allowlist so unrelated API and cloud-provider secrets are not inherited. The provider base URL appears in the command preview for diagnostics, so never put credentials in the URL. Local tasks receive project-boundary safety instructions, but those instructions are not an OS sandbox. Use a dedicated least-privileged target account, repository permissions, and network controls to enforce the real boundary.

Cancellation terminates the launched process tree and releases the Local AI slot. After an abnormal SSH or host failure, verify that no orphaned Codex process remains before retrying. On API restart, interrupted Local Codex runs and queued Local work fail closed for explicit recovery; standard Codex recovery behavior remains unchanged. Browser disconnects do not cancel server-side queue work, and stored output remains available after reconnecting.

The Local resource card samples the selected project machine rather than the inference server. It uses read-only, non-`sudo` probes, backs off transient failures, and leaves unsupported sensors blank.

## Queue Behavior

1. A request is queued against a project and model.
2. The worker processes each project's queue in order while running different project queues concurrently. Local Codex requests also share the configured global provider limit, which defaults to one.
3. Codex requests keep the existing `codex exec --json` path on the project machine. Prompts are streamed over stdin instead of placed in process arguments, which supports long requests on Windows and avoids exposing prompt text in process listings.
4. Local requests run `codex exec --json` on the same selected project machine and route inference to the selected Ollama, LM Studio, or llama.cpp profile.
5. Requests in the icon-only base tab keep the original behavior and start independent conversations. Named queue tabs retain separate standard and Local Codex session IDs and continue each runner only on the same project and machine.
6. The browser terminal is a separate reusable shell session per project and machine. It preserves shell state while the terminal stays open, but it does not automatically attach queued `codex exec` jobs to that terminal chat history.
7. If standard Codex commit generation is enabled and the request succeeds, a second Codex session runs with the commit model. This option is disabled for Local Codex in this release.
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

- Reliability: interrupted Local Codex runs are marked failed on API restart to avoid launching a duplicate unrestricted agent; queued Local work pauses for explicit recovery, while existing standard Codex recovery/requeue behavior remains unchanged.
- Maintainability: HTTP routes, persistence, command execution, file browsing, and queue processing are separated.
- Performance: UI progress uses polling to keep the first version simple; switch to SignalR if many users or sub-second updates are needed.
- Portability: Docker Compose keeps Apache, API, and SQLite data isolated; target-specific Codex setup stays on each execution machine. SSH folder browsing uses portable shell commands for Linux/macOS targets and PowerShell for Windows targets.
