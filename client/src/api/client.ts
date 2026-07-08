import type {
  ApiConfig,
  CodexRequest,
  CreateQueueRequest,
  FileContent,
  FileTreeEntry,
  Machine,
  MachineTest,
  Project,
  QueueDiagnostics,
  SaveMachineRequest,
  SaveProjectRequest,
  Session,
  TerminalCommandResult,
  UpdateQueueRequest,
} from './types'

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api'
const TOKEN_KEY = 'codex-queue-token'

export function apiWebSocketUrl(path: string) {
  const base = new URL(API_BASE, window.location.origin)
  const url = new URL(`${base.pathname.replace(/\/$/, '')}${path}`, base.origin)
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'
  const token = getStoredToken()
  if (token) {
    url.searchParams.set('access_token', token)
  }

  return url.toString()
}

export class ApiError extends Error {
  status: number

  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

export function getStoredToken() {
  return localStorage.getItem(TOKEN_KEY) ?? ''
}

export function storeToken(token: string) {
  if (token.trim()) {
    localStorage.setItem(TOKEN_KEY, token.trim())
  } else {
    localStorage.removeItem(TOKEN_KEY)
  }
}

async function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  const token = getStoredToken()
  if (token) {
    headers.set('Authorization', `Bearer ${token}`)
  }
  if (init.body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  const response = await fetch(`${API_BASE}${path}`, { ...init, headers })
  if (!response.ok) {
    let message = response.statusText
    try {
      const body = (await response.json()) as { error?: string }
      message = body.error ?? message
    } catch {
      // Keep the HTTP status text when the server did not return JSON.
    }
    throw new ApiError(message, response.status)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return (await response.json()) as T
}

export const api = {
  config: () => apiFetch<ApiConfig>('/config'),
  verifyToken: () => apiFetch<{ ok: boolean }>('/auth/verify', { method: 'POST' }),
  machines: () => apiFetch<Machine[]>('/machines'),
  saveMachine: (machine: SaveMachineRequest, id?: string) =>
    apiFetch<Machine>(id ? `/machines/${id}` : '/machines', {
      method: id ? 'PUT' : 'POST',
      body: JSON.stringify(machine),
    }),
  deleteMachine: (id: string) => apiFetch<void>(`/machines/${id}`, { method: 'DELETE' }),
  testMachine: (id: string) => apiFetch<MachineTest>(`/machines/${id}/test`, { method: 'POST' }),
  machineFolders: (id: string, path = '') =>
    apiFetch<FileTreeEntry[]>(`/machines/${id}/folders?path=${encodeURIComponent(path)}`),
  projects: () => apiFetch<Project[]>('/projects'),
  saveProject: (project: SaveProjectRequest, id?: string) =>
    apiFetch<Project>(id ? `/projects/${id}` : '/projects', {
      method: id ? 'PUT' : 'POST',
      body: JSON.stringify(project),
    }),
  deleteProject: (id: string) => apiFetch<void>(`/projects/${id}`, { method: 'DELETE' }),
  requests: (projectId?: string, includeDeleted = false) => {
    const params = new URLSearchParams()
    if (projectId) params.set('projectId', projectId)
    if (includeDeleted) params.set('includeDeleted', 'true')
    const query = params.toString()
    return apiFetch<CodexRequest[]>(query ? `/requests?${query}` : '/requests')
  },
  createRequest: (request: CreateQueueRequest) =>
    apiFetch<CodexRequest>('/requests', { method: 'POST', body: JSON.stringify(request) }),
  updateRequest: (id: string, request: UpdateQueueRequest) =>
    apiFetch<CodexRequest>(`/requests/${id}`, { method: 'PUT', body: JSON.stringify(request) }),
  reorderRequests: (projectId: string, requestIds: string[]) =>
    apiFetch<{ ok: boolean }>('/requests/reorder', { method: 'POST', body: JSON.stringify({ projectId, requestIds }) }),
  deleteRequest: (id: string) => apiFetch<void>(`/requests/${id}`, { method: 'DELETE' }),
  archiveRequest: (id: string) => apiFetch<CodexRequest>(`/requests/${id}/archive`, { method: 'POST' }),
  cancelRequest: (id: string) => apiFetch<{ ok: boolean }>(`/requests/${id}/cancel`, { method: 'POST' }),
  resumeRequest: (id: string) => apiFetch<{ ok: boolean }>(`/requests/${id}/resume`, { method: 'POST' }),
  queueDiagnostics: () => apiFetch<QueueDiagnostics>('/queue/diagnostics'),
  kickQueue: () => apiFetch<{ accepted: boolean }>('/queue/kick', { method: 'POST' }),
  sessions: () => apiFetch<Session[]>('/sessions'),
  tree: (projectId: string, path = '') =>
    apiFetch<FileTreeEntry[]>(`/projects/${projectId}/tree?path=${encodeURIComponent(path)}`),
  file: (projectId: string, path: string) =>
    apiFetch<FileContent>(`/projects/${projectId}/file?path=${encodeURIComponent(path)}`),
  terminal: (projectId: string, command: string) =>
    apiFetch<TerminalCommandResult>(`/projects/${projectId}/terminal`, {
      method: 'POST',
      body: JSON.stringify({ command }),
    }),
}
