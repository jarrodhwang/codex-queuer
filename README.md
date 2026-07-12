# Codex Queue

Codex Queue is a React + ASP.NET Core + SQLite web app for dispatching Codex CLI work to local or SSH target machines. The web server records queue state, output, session history, project grouping, file browsing, and optional separate commit sessions.

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

For a Windows SSH machine such as `192.168.0.50`, configure the machine as:

- Kind: `SSH`
- Platform: `Windows`
- Host: `192.168.0.50`
- User: `jarrod`
- SSH key: `/home/app/.ssh/zbook_fury`
- Working root: `C:\Users\jarrod`

Install Codex for the same Windows account configured as the SSH user. The Codex desktop app and Codex CLI are separate launch surfaces; verify the CLI from a fresh SSH connection with `ssh user@host codex --version`. Windows OpenSSH starts a non-interactive session, so Codex Queue restores the user and machine `PATH` and also checks the desktop app CLI directory plus the standard npm, Volta, Scoop, WinGet, Chocolatey, and Node locations. It supports the normal `codex.cmd` npm shim even when the SSH session does not provide `PATHEXT`. If the machine test reports that Codex is missing, sign in as that SSH user and run `npm.cmd install -g @openai/codex`, reconnect, and retry the machine test before queuing work. Windows PowerShell is forced to text output so SSH errors remain readable instead of appearing as CLIXML.

Codex runs over Windows SSH use `danger-full-access` because both native Windows sandbox modes can fail to initialize child processes in a non-interactive OpenSSH session with status `0xC0000142`. The queue still injects a strict project-root boundary into every prompt, but this is an instruction boundary rather than OS enforcement: the Codex process has the SSH user's filesystem and network access. Use a dedicated, least-privileged Windows account and restrict its NTFS permissions and SSH access. Local Windows runs continue to use the Codex-configured sandbox mode and desktop; Linux and macOS SSH runs retain `workspace-write` unless commit generation requires broader access.

## Queue Behavior

1. A request is queued against a project and model.
2. The worker processes each project's queue in order while running different project queues concurrently.
3. The worker runs `codex exec --json` on the project machine.
   Prompts are streamed over stdin instead of placed in process arguments, which supports long requests on Windows and avoids exposing prompt text in process listings.
4. Requests in the icon-only base tab keep the original behavior and start independent Codex threads.
5. Named queue tabs keep one Codex thread per project tab. After the first request establishes the thread, later requests in that tab continue it with `codex exec resume`.
6. The browser terminal is a separate reusable shell session per project and machine. It preserves shell state while the terminal stays open, but it does not automatically attach queued `codex exec` jobs to that terminal chat history.
7. If commit generation is enabled and the request succeeds, a second Codex session runs with the commit model.
8. Request and commit output are stored as separate runs and displayed together under the request details.

## Codex Session Model

- Queued work does not talk to the Codex desktop app session. It launches Codex CLI directly on the target machine.
- A request in the base queue starts a fresh Codex CLI thread. It does not inherit the browser terminal session or any Codex Desktop chat context.
- Named tabs persist their own Codex CLI thread ID and reuse it for later requests in that tab. Tabs are isolated by project.
- Separate commit session means exactly that: the follow-up commit prompt runs in a different Codex session and cannot see the earlier request chat unless you disable separate commit sessions.
- Deleting an inactive named tab preserves its request records by moving them to the base view. Tabs with active requests must be completed, cancelled, or cleared first.

## Security Notes

- Set `CQ_API_TOKEN` in `.env` for any non-private deployment.
- The app can trigger shell commands through Codex and SSH. Do not expose port `6767` without network controls and a strong token.
- SSH uses batch mode and key-based authentication only; passwords are not stored. The Docker host-local default requires the same SSH hardening as any other target machine.

## Practical Quality Notes

- Reliability: interrupted running jobs are marked failed on API restart; queued jobs remain queued.
- Maintainability: HTTP routes, persistence, command execution, file browsing, and queue processing are separated.
- Performance: UI progress uses polling to keep the first version simple; switch to SignalR if many users or sub-second updates are needed.
- Portability: Docker Compose keeps Apache, API, and SQLite data isolated; target-specific Codex setup stays on each execution machine. SSH folder browsing uses portable shell commands for Linux/macOS targets and PowerShell for Windows targets.
