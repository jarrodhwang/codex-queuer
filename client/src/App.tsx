import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ClipboardEvent, DragEvent, FormEvent, ReactNode } from 'react'
import { createPortal, flushSync } from 'react-dom'
import {
  ArrowDown,
  ArrowUp,
  Check,
  ChevronDown,
  ChevronRight,
  ClipboardList,
  Code2,
  FileText,
  Folder,
  FolderOpen,
  FolderPlus,
  Gauge,
  GitBranch,
  GitCommit,
  GripVertical,
  History,
  Image as ImageIcon,
  Menu,
  Monitor,
  Moon,
  Pencil,
  Play,
  Plus,
  RefreshCcw,
  Server,
  Settings,
  Sun,
  Square,
  Terminal as TerminalIcon,
  Trash2,
  X,
} from 'lucide-react'
import { ApiError, api, apiUrl, getStoredToken, storeToken } from '@/api/client'
import type {
  ApiConfig,
  CodexRun,
  CodexRequest,
  RunKind,
  FileContent,
  FileTreeEntry,
  GitStatus,
  Machine,
  MachineKind,
  MachineRateLimits,
  MachinePlatform,
  RateLimit,
  ModelOption,
  Project,
  QueueAttachment,
  QueueDiagnostics,
  QueueTab,
  SaveMachineRequest,
  SaveProjectRequest,
  UpdateQueueRequest,
} from '@/api/types'
import { FieldLabel, GlassButton, GlassDropdownSelect, GlassInput, GlassPanel, GlassSelect, GlassTextarea } from '@/components/einui/Glass'
import { ConfirmDialog } from '@/components/einui/ConfirmDialog'
import { ProgressLine, StatusBadge } from '@/components/einui/Status'
import { formatDate, shortId } from '@/lib/utils'
import './App.css'

type OpenFile = FileContent & {
  key: string
  projectId: string
  projectName: string
}

type ModelValue = {
  model: string
  effort: string
  speed: string
}

type ProjectModelDefaults = {
  requestModel: ModelValue
  commitModel: ModelValue
  generateCommit: boolean
  separateCommitSession: boolean
}

type RightRailView = 'files' | 'git'
type ColorTheme = 'light' | 'dark'
type AttachmentKind = 'image' | 'json' | 'csv' | 'text' | 'binary'

type AttachmentInsight = {
  kind: AttachmentKind
  label: string
  preview?: string
  detail?: string
  imageUrl?: string
}

type LocalQueueAttachment = QueueAttachment & {
  insight: AttachmentInsight
}

const defaultModels: ModelOption[] = [
  { label: 'GPT-5.6 Sol', model: 'gpt-5.6-sol', supportsPriority: true },
  { label: 'GPT-5.6 Terra', model: 'gpt-5.6-terra', supportsPriority: true },
  { label: 'GPT-5.6 Luna', model: 'gpt-5.6-luna', supportsPriority: true },
  { label: 'GPT-5.5', model: 'gpt-5.5', supportsPriority: true },
  { label: 'GPT-5.4', model: 'gpt-5.4', supportsPriority: true },
  { label: 'GPT-5.4 Mini', model: 'gpt-5.4-mini', supportsPriority: false },
  { label: 'GPT-5.3 Codex Spark', model: 'gpt-5.3-codex-spark', supportsPriority: false },
]

const appIconUrl = `${import.meta.env.BASE_URL}app-icon.png`

const emptyMachine: SaveMachineRequest = {
  name: '',
  kind: 'Ssh',
  host: '',
  port: 22,
  userName: '',
  sshKeyPath: '',
  workingRoot: '',
  platform: 'Auto',
}

const themeStorageKey = 'codex-queue-theme'

function getInitialTheme(): ColorTheme {
  try {
    const storedTheme = window.localStorage.getItem(themeStorageKey)
    if (storedTheme === 'light' || storedTheme === 'dark') {
      return storedTheme
    }
  } catch {
    // Storage can be unavailable in privacy-restricted browsing contexts.
  }

  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}

function requestCompletionNotificationPermission() {
  if (typeof Notification === 'undefined' || Notification.permission !== 'default') {
    return
  }

  void Notification.requestPermission().catch(() => undefined)
}

function notifyRequestSucceeded(request: CodexRequest) {
  if (typeof Notification === 'undefined' || Notification.permission !== 'granted') {
    return
  }

  const notification = new Notification('Codex Queue item succeeded', {
    body: `${request.projectName}: ${clipPreview(request.prompt).replace(/\s+/g, ' ').slice(0, 160)}`,
    tag: `codex-queue-request-${request.id}`,
  })
  notification.onclick = () => {
    window.focus()
    notification.close()
  }
}

function useMediaQuery(query: string) {
  const getMatches = () => typeof window !== 'undefined' && window.matchMedia(query).matches
  const [matches, setMatches] = useState(getMatches)

  useEffect(() => {
    if (typeof window === 'undefined') {
      return
    }

    const mediaQuery = window.matchMedia(query)
    const updateMatches = () => setMatches(mediaQuery.matches)
    updateMatches()
    mediaQuery.addEventListener('change', updateMatches)
    return () => mediaQuery.removeEventListener('change', updateMatches)
  }, [query])

  return matches
}

function projectSavePayload(project: Project, overrides: Partial<SaveProjectRequest> = {}): SaveProjectRequest {
  return {
    name: project.name,
    path: project.path,
    machineId: project.machineId,
    defaultModel: project.defaultModel,
    defaultModelEffort: project.defaultModelEffort,
    defaultModelSpeed: project.defaultModelSpeed,
    defaultCommitModel: project.defaultCommitModel,
    defaultCommitModelEffort: project.defaultCommitModelEffort,
    defaultCommitModelSpeed: project.defaultCommitModelSpeed,
    defaultGenerateCommit: project.defaultGenerateCommit ?? true,
    defaultSeparateCommitSession: project.defaultSeparateCommitSession ?? false,
    separateQueuesByTab: project.separateQueuesByTab ?? false,
    ...overrides,
  }
}

function modelValueFromDefaults(model: string | null | undefined, effort: string | null | undefined, speed: string | null | undefined, fallback: ModelOption): ModelValue {
  return {
    model: model?.trim() || fallback.model,
    effort: effort?.trim() || 'medium',
    speed: speed?.trim() || (fallback.supportsPriority ? 'priority' : 'normal'),
  }
}

function projectModelDefaults(project: Project, models: ModelOption[]): ProjectModelDefaults {
  const requestFallback = models[0] ?? defaultModels[0]
  const commitFallback = models[1] ?? requestFallback
  return {
    requestModel: modelValueFromDefaults(project.defaultModel, project.defaultModelEffort, project.defaultModelSpeed, requestFallback),
    commitModel: modelValueFromDefaults(project.defaultCommitModel, project.defaultCommitModelEffort, project.defaultCommitModelSpeed, commitFallback),
    generateCommit: project.defaultGenerateCommit ?? true,
    separateCommitSession: project.defaultSeparateCommitSession ?? false,
  }
}

async function readQueueAttachment(file: File): Promise<LocalQueueAttachment> {
  if (file.size > 5_000_000) {
    throw new Error(`${file.name} is larger than 5 MB.`)
  }

  const buffer = await file.arrayBuffer()
  const contentBase64 = arrayBufferToBase64(buffer)
  return {
    name: file.name,
    contentType: file.type || 'application/octet-stream',
    size: file.size,
    contentBase64,
    insight: analyzeAttachment(file.name, file.type || 'application/octet-stream', buffer, contentBase64),
  }
}

function normalizeAttachmentFile(file: File, index: number) {
  if (file.name.trim()) {
    return file
  }

  const extension = extensionForContentType(file.type)
  return new File([file], `pasted-file-${index + 1}${extension}`, {
    type: file.type || 'application/octet-stream',
    lastModified: file.lastModified,
  })
}

function extensionForContentType(contentType: string) {
  if (contentType === 'image/png') return '.png'
  if (contentType === 'image/jpeg') return '.jpg'
  if (contentType === 'image/gif') return '.gif'
  if (contentType === 'image/webp') return '.webp'
  if (contentType === 'text/plain') return '.txt'
  if (contentType === 'text/csv') return '.csv'
  if (contentType === 'application/json') return '.json'
  return ''
}

function arrayBufferToBase64(buffer: ArrayBuffer) {
  const bytes = new Uint8Array(buffer)
  let binary = ''
  const chunkSize = 0x8000
  for (let index = 0; index < bytes.length; index += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(index, index + chunkSize))
  }

  return window.btoa(binary)
}

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function attachmentPayload(attachments: LocalQueueAttachment[]): QueueAttachment[] {
  return attachments.map(({ name, contentType, size, contentBase64 }) => ({ name, contentType, size, contentBase64 }))
}

function analyzeAttachment(name: string, contentType: string, buffer: ArrayBuffer, contentBase64: string): AttachmentInsight {
  if (contentType.startsWith('image/')) {
    return {
      kind: 'image',
      label: 'Image',
      detail: `${contentType} · ${formatBytes(buffer.byteLength)}`,
      imageUrl: `data:${contentType};base64,${contentBase64}`,
    }
  }

  if (!isTextLikeAttachment(name, contentType)) {
    return {
      kind: 'binary',
      label: 'File',
      detail: contentType || 'binary file',
    }
  }

  const text = new TextDecoder('utf-8', { fatal: false }).decode(buffer)
  const clippedText = clipPreview(text)
  if (isJsonAttachment(name, contentType)) {
    const parsed = tryParseJson(text.trim())
    if (parsed !== undefined) {
      return {
        kind: 'json',
        label: 'JSON',
        detail: jsonSummary(parsed),
        preview: JSON.stringify(parsed, null, 2).slice(0, 2400),
      }
    }
  }

  if (isCsvAttachment(name, contentType)) {
    const summary = csvSummary(text)
    return {
      kind: 'csv',
      label: 'CSV',
      detail: summary.detail,
      preview: summary.preview,
    }
  }

  return {
    kind: 'text',
    label: textAttachmentLabel(name, contentType),
    detail: textLineSummary(text),
    preview: clippedText,
  }
}

function clipPreview(text: string) {
  const normalized = text.replace(/\r\n/g, '\n').trim()
  return normalized.length > 2400 ? `${normalized.slice(0, 2400)}\n...truncated...` : normalized
}

function isTextLikeAttachment(name: string, contentType: string) {
  const lowerName = name.toLowerCase()
  return contentType.startsWith('text/')
    || contentType.includes('json')
    || contentType.includes('xml')
    || ['.csv', '.json', '.xml', '.xaml', '.html', '.css', '.js', '.ts', '.tsx', '.py', '.cs', '.c', '.cpp', '.h', '.md', '.txt']
      .some((extension) => lowerName.endsWith(extension))
}

function isJsonAttachment(name: string, contentType: string) {
  return contentType.includes('json') || name.toLowerCase().endsWith('.json')
}

function isCsvAttachment(name: string, contentType: string) {
  return contentType === 'text/csv' || name.toLowerCase().endsWith('.csv')
}

function textAttachmentLabel(name: string, contentType: string) {
  if (name.toLowerCase().endsWith('.md')) return 'Markdown'
  if (contentType.includes('xml') || name.toLowerCase().endsWith('.xml')) return 'XML'
  if (name.match(/\.(ts|tsx|js|css|py|cs|cpp|c|h)$/i)) return 'Code'
  return 'Text'
}

function textLineSummary(text: string) {
  const lines = text.replace(/\r\n/g, '\n').split('\n')
  const nonEmpty = lines.filter((line) => line.trim()).length
  return `${nonEmpty.toLocaleString()} non-empty ${nonEmpty === 1 ? 'line' : 'lines'}`
}

function jsonSummary(value: unknown): string {
  if (Array.isArray(value)) {
    return `${value.length.toLocaleString()} ${value.length === 1 ? 'item' : 'items'}`
  }

  if (isRecord(value)) {
    const keys = Object.keys(value)
    return `${keys.length.toLocaleString()} ${keys.length === 1 ? 'key' : 'keys'}${keys.length > 0 ? ` · ${keys.slice(0, 4).join(', ')}` : ''}`
  }

  return typeof value
}

function csvSummary(text: string) {
  const rows = text.replace(/\r\n/g, '\n').split('\n').filter((line) => line.trim())
  const header = rows[0] ? parseCsvLine(rows[0]) : []
  const sampleRows = rows.slice(1, 5).map(parseCsvLine)
  const widths = header.map((column, index) => Math.max(column.length, ...sampleRows.map((row) => row[index]?.length ?? 0))).map((width) => Math.min(width, 28))
  const formatRow = (row: string[]) => row.map((cell, index) => (cell ?? '').slice(0, widths[index] ?? 16).padEnd(widths[index] ?? 16)).join('  ').trimEnd()
  const preview = [formatRow(header), widths.map((width) => '-'.repeat(width)).join('  '), ...sampleRows.map(formatRow)]
    .filter(Boolean)
    .join('\n')
  return {
    detail: `${Math.max(rows.length - 1, 0).toLocaleString()} rows · ${header.length.toLocaleString()} columns`,
    preview,
  }
}

function parseCsvLine(line: string) {
  const cells: string[] = []
  let current = ''
  let quoted = false
  for (let index = 0; index < line.length; index += 1) {
    const character = line[index]
    if (character === '"') {
      if (quoted && line[index + 1] === '"') {
        current += '"'
        index += 1
      } else {
        quoted = !quoted
      }
      continue
    }

    if (character === ',' && !quoted) {
      cells.push(current.trim())
      current = ''
      continue
    }

    current += character
  }
  cells.push(current.trim())
  return cells
}

function isOptimisticRequest(id: string) {
  return id.startsWith('optimistic:')
}

function compareQueueRequests(left: CodexRequest, right: CodexRequest) {
  return (left.queueOrder - right.queueOrder) || Date.parse(left.createdAt) - Date.parse(right.createdAt)
}

function prioritizedQueueOrderById(requests: CodexRequest[], queuedPriorityIds: string[] = []) {
  const requestsById = new Map(requests.map((request) => [request.id, request]))
  const priorityQueued = queuedPriorityIds
    .map((id) => requestsById.get(id))
    .filter((request): request is CodexRequest => request !== undefined && request.status === 'Queued')
  const priorityIdSet = new Set(priorityQueued.map((request) => request.id))
  const active = requests
    .filter((request) => request.status === 'Running' || request.status === 'CancelRequested')
    .toSorted((left, right) => (left.queueOrder - right.queueOrder) || Date.parse(left.startedAt ?? left.createdAt) - Date.parse(right.startedAt ?? right.createdAt))
  const queued = requests
    .filter((request) => request.status === 'Queued' && !priorityIdSet.has(request.id))
    .toSorted((left, right) => (left.queueOrder - right.queueOrder) || Date.parse(left.createdAt) - Date.parse(right.createdAt))
  const slots = [...active, ...priorityQueued, ...queued]
    .map((request) => request.queueOrder)
    .toSorted((left, right) => left - right)
  return new Map([...active, ...priorityQueued, ...queued].map((request, index) => [request.id, slots[index] ?? request.queueOrder]))
}

function requestOverrideConfirmed(request: CodexRequest, override: Partial<CodexRequest>) {
  if (override.deletedAt !== undefined) {
    return Boolean(request.deletedAt)
  }

  if (override.archivedAt !== undefined) {
    return Boolean(request.archivedAt)
  }

  if (override.status === 'CancelRequested') {
    return request.status === 'CancelRequested' || request.status === 'Cancelled'
  }

  if (override.status === 'Queued') {
    return request.status === 'Queued' || request.status === 'Running'
  }

  if (override.status !== undefined && request.status !== override.status) {
    return false
  }

  if (override.queueOrder !== undefined && request.queueOrder !== override.queueOrder) {
    return false
  }

  if (override.prompt !== undefined && request.prompt !== override.prompt) {
    return false
  }

  return true
}

function App() {
  const [theme, setTheme] = useState<ColorTheme>(getInitialTheme)
  const [config, setConfig] = useState<ApiConfig>({ requiresToken: false, models: defaultModels })
  const [machines, setMachines] = useState<Machine[]>([])
  const [projects, setProjects] = useState<Project[]>([])
  const [queueTabs, setQueueTabs] = useState<QueueTab[]>([])
  const [requests, setRequests] = useState<CodexRequest[]>([])
  const [queueDiagnostics, setQueueDiagnostics] = useState<QueueDiagnostics | null>(null)
  const [selectedProjectId, setSelectedProjectId] = useState('')
  const [rightOpen, setRightOpen] = useState(false)
  const [rightRailView, setRightRailView] = useState<RightRailView>('files')
  const [authBlocked, setAuthBlocked] = useState(false)
  const [error, setError] = useState('')
  const [openFiles, setOpenFiles] = useState<OpenFile[]>([])
  const [activeFileKey, setActiveFileKey] = useState<string | null>(null)
  const [liveNow, setLiveNow] = useState(() => Date.now())
  const [deleteRequestId, setDeleteRequestId] = useState<string | null>(null)
  const pendingRequestOverridesRef = useRef(new Map<string, Partial<CodexRequest>>())
  const liveRequestSequenceRef = useRef(0)
  const hasLoadedLiveRequestsRef = useRef(false)
  const previousRequestStatusRef = useRef<Map<string, CodexRequest['status']> | null>(null)

  useEffect(() => {
    document.documentElement.dataset.theme = theme
    document.documentElement.style.colorScheme = theme
    document.querySelector('meta[name="theme-color"]')?.setAttribute('content', theme === 'dark' ? '#070b16' : '#f8fbff')
    try {
      window.localStorage.setItem(themeStorageKey, theme)
    } catch {
      // The selected theme still applies for this session when storage is unavailable.
    }
  }, [theme])

  const selectedProject = projects.find((project) => project.id === selectedProjectId) ?? projects[0]
  const activeFile = openFiles.find((file) => file.key === activeFileKey)
  const hasLiveTimers = useMemo(
    () => requests.some((request) =>
      request.status === 'Running' ||
      request.status === 'UsageLimited' ||
      Boolean(request.retryAfter) ||
      request.runs.some((run) => run.status === 'Running' || run.status === 'UsageLimited' || Boolean(run.retryAfter)),
    ),
    [requests],
  )

  const loadStatic = useCallback(async () => {
    const nextConfig = await api.config()
    setConfig(nextConfig)
    if (nextConfig.requiresToken && !getStoredToken()) {
      setAuthBlocked(true)
      return
    }
    const [nextMachines, nextProjects, nextQueueTabs] = await Promise.all([api.machines(), api.projects(), api.queueTabs()])
    setMachines(nextMachines)
    setProjects(nextProjects)
    setQueueTabs(nextQueueTabs)
    setSelectedProjectId((current) => current || nextProjects[0]?.id || '')
  }, [])

  const loadLive = useCallback(async () => {
    const requestSequence = ++liveRequestSequenceRef.current
    const [nextRequests, nextDiagnostics] = await Promise.all([
      api.requests(undefined, true),
      api.queueDiagnostics().catch(() => null),
    ])
    if (requestSequence !== liveRequestSequenceRef.current) {
      return
    }
    setRequests((current) => {
      const requestsWithOverrides = nextRequests.map((request) => {
        const override = pendingRequestOverridesRef.current.get(request.id)
        if (!override) return request
        if (requestOverrideConfirmed(request, override)) {
          pendingRequestOverridesRef.current.delete(request.id)
        }
        return { ...request, ...override }
      })
      const optimisticPreviews = current.filter((request) => isOptimisticRequest(request.id))
      return [
        ...requestsWithOverrides,
        ...optimisticPreviews.filter((preview) => !requestsWithOverrides.some((request) => request.id === preview.id)),
      ]
    })
    setQueueDiagnostics(nextDiagnostics)
    hasLoadedLiveRequestsRef.current = true
  }, [])

  const handleApiError = useCallback((cause: unknown) => {
    if (cause instanceof ApiError && cause.status === 401) {
      setAuthBlocked(true)
      return
    }
    setError(cause instanceof Error ? cause.message : 'Request failed.')
  }, [])

  const refreshLiveInBackground = useCallback(() => {
    void loadLive().catch(handleApiError)
  }, [handleApiError, loadLive])

  const applyRequestsImmediately = useCallback((updater: (current: CodexRequest[]) => CodexRequest[]) => {
    flushSync(() => {
      setRequests(updater)
    })
  }, [])

  const addCreatedRequest = useCallback((request: CodexRequest, replaceId?: string) => {
    applyRequestsImmediately((current) => {
      const withoutCreated = current.filter((item) => item.id !== request.id && item.id !== replaceId)
      return [...withoutCreated, request]
    })
    if (!isOptimisticRequest(request.id)) {
      refreshLiveInBackground()
    }
  }, [applyRequestsImmediately, refreshLiveInBackground])

  const discardRequestPreview = useCallback((id: string) => {
    applyRequestsImmediately((current) => current.filter((request) => request.id !== id))
  }, [applyRequestsImmediately])

  useEffect(() => {
    loadStatic().then(() => loadLive()).catch(handleApiError)
  }, [handleApiError, loadLive, loadStatic])

  useEffect(() => {
    if (!hasLoadedLiveRequestsRef.current) return

    const previousStatuses = previousRequestStatusRef.current
    const nextStatuses = new Map(requests.map((request) => [request.id, request.status]))
    previousRequestStatusRef.current = nextStatuses
    if (!previousStatuses) return

    requests.forEach((request) => {
      if (request.status === 'Succeeded' && previousStatuses.get(request.id) !== 'Succeeded') {
        notifyRequestSucceeded(request)
      }
    })
  }, [requests])

  useEffect(() => {
    if (authBlocked) return
    const timer = window.setInterval(() => {
      loadLive().catch(handleApiError)
    }, 2200)
    return () => window.clearInterval(timer)
  }, [authBlocked, handleApiError, loadLive])

  useEffect(() => {
    if (!hasLiveTimers) return
    const timer = window.setInterval(() => {
      setLiveNow(Date.now())
    }, 1000)
    return () => window.clearInterval(timer)
  }, [hasLiveTimers])

  const refreshAll = async () => {
    setError('')
    try {
      await loadStatic()
      await loadLive()
    } catch (cause) {
      handleApiError(cause)
    }
  }

  const openFile = async (project: Project, path: string) => {
    setError('')
    try {
      const content = await api.file(project.id, path)
      const key = `${project.id}:${path}`
      setOpenFiles((current) => {
        const existing = current.filter((file) => file.key !== key)
        return [...existing, { ...content, key, projectId: project.id, projectName: project.name }]
      })
      setActiveFileKey(key)
    } catch (cause) {
      handleApiError(cause)
    }
  }

  const closeFile = (key: string) => {
    setOpenFiles((current) => current.filter((file) => file.key !== key))
    setActiveFileKey((current) => {
      if (current !== key) return current
      const remaining = openFiles.filter((file) => file.key !== key)
      return remaining.at(-1)?.key ?? null
    })
  }

  const renameProject = async (project: Project, name: string) => {
    setError('')
    try {
      const updated = await api.saveProject(projectSavePayload(project, { name }), project.id)
      setProjects((current) => current.map((item) => (item.id === updated.id ? updated : item)))
      setOpenFiles((current) => current.map((file) => (file.projectId === updated.id ? { ...file, projectName: updated.name } : file)))
      await loadLive()
    } catch (cause) {
      handleApiError(cause)
      throw cause
    }
  }

  const updateProjectDefaults = async (project: Project, defaults: ProjectModelDefaults) => {
    setError('')
    try {
      const updated = await api.saveProject(projectSavePayload(project, {
        defaultModel: defaults.requestModel.model,
        defaultModelEffort: defaults.requestModel.effort,
        defaultModelSpeed: defaults.requestModel.speed,
        defaultCommitModel: defaults.commitModel.model,
        defaultCommitModelEffort: defaults.commitModel.effort,
        defaultCommitModelSpeed: defaults.commitModel.speed,
        defaultGenerateCommit: defaults.generateCommit,
        defaultSeparateCommitSession: defaults.separateCommitSession,
      }), project.id)
      setProjects((current) => current.map((item) => (item.id === updated.id ? updated : item)))
    } catch (cause) {
      handleApiError(cause)
      throw cause
    }
  }

  const updateProjectQueueMode = async (project: Project, separateQueuesByTab: boolean) => {
    setError('')
    try {
      const updated = await api.saveProject(projectSavePayload(project, { separateQueuesByTab }), project.id)
      setProjects((current) => current.map((item) => (item.id === updated.id ? updated : item)))
      await loadLive()
    } catch (cause) {
      handleApiError(cause)
      throw cause
    }
  }

  const removeProject = async (project: Project) => {
    setError('')
    try {
      await api.deleteProject(project.id)
      const remainingProjects = projects.filter((item) => item.id !== project.id)
      const remainingOpenFiles = openFiles.filter((file) => file.projectId !== project.id)
      setProjects(remainingProjects)
      setQueueTabs((current) => current.filter((tab) => tab.projectId !== project.id))
      setSelectedProjectId(remainingProjects[0]?.id ?? '')
      setOpenFiles(remainingOpenFiles)
      setActiveFileKey((current) => (current?.startsWith(`${project.id}:`) ? remainingOpenFiles.at(-1)?.key ?? null : current))
      await loadLive()
      return true
    } catch (cause) {
      handleApiError(cause)
      return false
    }
  }

  const kickQueue = async () => {
    setError('')
    try {
      await api.kickQueue()
      refreshLiveInBackground()
    } catch (cause) {
      handleApiError(cause)
    }
  }

  const createQueueTab = async (projectId: string, name: string) => {
    setError('')
    try {
      const created = await api.createQueueTab(projectId, name)
      setQueueTabs((current) => [...current.filter((tab) => tab.id !== created.id), created])
      return created
    } catch (cause) {
      handleApiError(cause)
      throw cause
    }
  }

  const renameQueueTab = async (id: string, name: string) => {
    setError('')
    try {
      const updated = await api.renameQueueTab(id, name)
      setQueueTabs((current) => current.map((tab) => (tab.id === id ? updated : tab)))
      setRequests((current) => current.map((request) => (request.queueTabId === id ? { ...request, queueTabName: updated.name } : request)))
      return updated
    } catch (cause) {
      handleApiError(cause)
      throw cause
    }
  }

  const deleteQueueTab = async (id: string) => {
    setError('')
    try {
      await api.deleteQueueTab(id)
      setQueueTabs((current) => current.filter((tab) => tab.id !== id))
      refreshLiveInBackground()
    } catch (cause) {
      handleApiError(cause)
      throw cause
    }
  }

  const toggleRightRail = (view: RightRailView) => {
    setRightRailView(view)
    setRightOpen((open) => (rightRailView === view ? !open : true))
  }

  const archiveRequest = async (id: string) => {
    setError('')
    const archivedAt = new Date().toISOString()
    pendingRequestOverridesRef.current.set(id, { archivedAt })
    applyRequestsImmediately((current) => current.map((request) => (request.id === id ? { ...request, archivedAt } : request)))
    try {
      const updated = await api.archiveRequest(id)
      setRequests((current) => current.map((request) => (request.id === id ? { ...updated, archivedAt: updated.archivedAt ?? archivedAt } : request)))
      refreshLiveInBackground()
    } catch (cause) {
      pendingRequestOverridesRef.current.delete(id)
      handleApiError(cause)
      refreshLiveInBackground()
    }
  }

  const archiveRequests = async (ids: string[]) => {
    if (ids.length === 0) return

    setError('')
    const archivedAt = new Date().toISOString()
    const idSet = new Set(ids)
    ids.forEach((id) => pendingRequestOverridesRef.current.set(id, { archivedAt }))
    applyRequestsImmediately((current) => current.map((request) => (idSet.has(request.id) ? { ...request, archivedAt } : request)))
    try {
      const updatedRequests = await Promise.all(ids.map((id) => api.archiveRequest(id)))
      const updatedById = new Map(updatedRequests.map((request) => [request.id, { ...request, archivedAt: request.archivedAt ?? archivedAt }]))
      setRequests((current) => current.map((request) => updatedById.get(request.id) ?? request))
      refreshLiveInBackground()
    } catch (cause) {
      ids.forEach((id) => pendingRequestOverridesRef.current.delete(id))
      handleApiError(cause)
      refreshLiveInBackground()
    }
  }

  const updateRequest = async (id: string, request: UpdateQueueRequest) => {
    setError('')
    const { attachments, ...requestFields } = request
    const optimisticUpdate: Partial<CodexRequest> = {
      ...requestFields,
    }
    if (attachments) {
      optimisticUpdate.attachments = attachments.map(({ name, contentType, size }) => ({ name, contentType, size }))
    }
    pendingRequestOverridesRef.current.set(id, optimisticUpdate)
    applyRequestsImmediately((current) => current.map((item) => (item.id === id
      ? {
          ...item,
          ...optimisticUpdate,
          attachments: optimisticUpdate.attachments ?? item.attachments,
        }
      : item)))
    try {
      const updated = await api.updateRequest(id, request)
      setRequests((current) => current.map((item) => (item.id === id ? updated : item)))
      refreshLiveInBackground()
    } catch (cause) {
      pendingRequestOverridesRef.current.delete(id)
      handleApiError(cause)
      refreshLiveInBackground()
      throw cause
    }
  }

  const reorderRequests = async (projectId: string, requestIds: string[]) => {
    setError('')
    const separateQueuesByTab = projects.find((project) => project.id === projectId)?.separateQueuesByTab ?? false
    const queueTabId = requests.find((request) => request.id === requestIds[0])?.queueTabId ?? null
    const priorityRequests = requests.filter((request) =>
      request.projectId === projectId &&
      (!separateQueuesByTab || (request.queueTabId ?? null) === queueTabId) &&
      !request.deletedAt &&
      !request.archivedAt &&
      (request.status === 'Queued' || request.status === 'Running' || request.status === 'CancelRequested'),
    )
    const orderById = prioritizedQueueOrderById(priorityRequests, requestIds)
    requestIds.forEach((id) => pendingRequestOverridesRef.current.set(id, { queueOrder: orderById.get(id) }))
    applyRequestsImmediately((current) => current.map((request) => (
      request.projectId === projectId && orderById.has(request.id)
        ? { ...request, queueOrder: orderById.get(request.id) ?? request.queueOrder }
        : request
    )))
    try {
      await api.reorderRequests(projectId, requestIds)
      refreshLiveInBackground()
    } catch (cause) {
      requestIds.forEach((id) => pendingRequestOverridesRef.current.delete(id))
      handleApiError(cause)
      refreshLiveInBackground()
      throw cause
    }
  }

  const deleteRequest = async (id: string) => {
    setError('')
    const deletedAt = new Date().toISOString()
    pendingRequestOverridesRef.current.set(id, { deletedAt })
    applyRequestsImmediately((current) => current.map((item) => (item.id === id ? { ...item, deletedAt } : item)))
    try {
      await api.deleteRequest(id)
      refreshLiveInBackground()
    } catch (cause) {
      pendingRequestOverridesRef.current.delete(id)
      handleApiError(cause)
      refreshLiveInBackground()
    }
  }

  const requestToDelete = useMemo(() => requests.find((request) => request.id === deleteRequestId) ?? null, [deleteRequestId, requests])

  const cancelRequest = async (id: string) => {
    setError('')
    const optimisticUpdate: Partial<CodexRequest> = {
      status: 'CancelRequested',
      retryAfter: null,
      retryReason: null,
    }
    pendingRequestOverridesRef.current.set(id, optimisticUpdate)
    applyRequestsImmediately((current) => current.map((request) => (request.id === id
      ? {
          ...request,
          ...optimisticUpdate,
        }
      : request)))
    try {
      await api.cancelRequest(id)
      refreshLiveInBackground()
    } catch (cause) {
      pendingRequestOverridesRef.current.delete(id)
      handleApiError(cause)
      refreshLiveInBackground()
    }
  }

  const resumeRequest = async (id: string) => {
    setError('')
    const optimisticUpdate: Partial<CodexRequest> = {
      status: 'Queued',
      retryAfter: null,
      retryReason: null,
      error: null,
      startedAt: null,
      finishedAt: null,
    }
    const target = requests.find((request) => request.id === id)
    const separateQueuesByTab = projects.find((project) => project.id === target?.projectId)?.separateQueuesByTab ?? false
    const priorityRequests = target
      ? requests
          .map((request) => (request.id === id ? { ...request, ...optimisticUpdate } : request))
          .filter((request) =>
            request.projectId === target.projectId &&
            (!separateQueuesByTab || (request.queueTabId ?? null) === (target.queueTabId ?? null)) &&
            !request.deletedAt &&
            !request.archivedAt &&
            (request.status === 'Queued' || request.status === 'Running' || request.status === 'CancelRequested'),
          )
      : []
    const orderById = prioritizedQueueOrderById(priorityRequests, [id])
    const optimisticIds = new Set(orderById.keys())
    if (!optimisticIds.has(id)) {
      optimisticIds.add(id)
    }
    optimisticIds.forEach((requestId) => {
      const queueOrder = orderById.get(requestId)
      pendingRequestOverridesRef.current.set(requestId, requestId === id ? { ...optimisticUpdate, queueOrder } : { queueOrder })
    })
    applyRequestsImmediately((current) => current.map((request) => {
      const queueOrder = orderById.get(request.id)
      if (request.id === id) {
        return { ...request, ...optimisticUpdate, queueOrder: queueOrder ?? request.queueOrder }
      }

      return queueOrder === undefined ? request : { ...request, queueOrder }
    }))
    try {
      await api.resumeRequest(id)
      refreshLiveInBackground()
    } catch (cause) {
      optimisticIds.forEach((requestId) => pendingRequestOverridesRef.current.delete(requestId))
      handleApiError(cause)
      refreshLiveInBackground()
    }
  }

  if (authBlocked) {
    return (
      <AuthScreen
        onAuthed={async () => {
          setAuthBlocked(false)
          await refreshAll()
        }}
      />
    )
  }

  return (
    <div className="app-shell" data-right-open={rightOpen ? 'true' : 'false'}>
      <LeftSidebar
        theme={theme}
        onToggleTheme={() => setTheme((current) => (current === 'dark' ? 'light' : 'dark'))}
        machines={machines}
        projects={projects}
        requests={requests}
        now={liveNow}
        selectedProjectId={selectedProject?.id ?? ''}
        onSelectProject={setSelectedProjectId}
        onRenameProject={renameProject}
        onRemoveProject={removeProject}
        onChanged={refreshAll}
        onError={handleApiError}
      />

      <main className="main-surface">
        {openFiles.length > 0 && (
          <div className="tab-strip" aria-label="Open files">
            {openFiles.map((file) => (
              <div key={file.key} className={`file-tab ${file.key === activeFileKey ? 'active' : ''}`} onClick={() => setActiveFileKey(file.key)}>
                <Code2 size={15} />
                <span className="truncate">{file.path}</span>
                <button type="button" onClick={(event) => { event.stopPropagation(); closeFile(file.key) }} aria-label={`Close ${file.path}`}>
                  <X size={14} />
                </button>
              </div>
            ))}
          </div>
        )}

        {activeFile ? (
          <CodeViewer file={activeFile} />
        ) : (
        <QueueWorkspace
            config={config}
            selectedProject={selectedProject}
            queueTabs={queueTabs}
            requests={requests}
            diagnostics={queueDiagnostics}
            now={liveNow}
            onCreated={addCreatedRequest}
            onDiscardRequestPreview={discardRequestPreview}
            onCancel={cancelRequest}
            onResume={resumeRequest}
            onArchiveRequest={archiveRequest}
            onArchiveRequests={archiveRequests}
            onUpdateRequest={updateRequest}
            onReorderRequests={reorderRequests}
            onDeleteRequest={setDeleteRequestId}
            onUpdateProjectDefaults={updateProjectDefaults}
            onUpdateProjectQueueMode={updateProjectQueueMode}
            onCreateQueueTab={createQueueTab}
            onRenameQueueTab={renameQueueTab}
            onDeleteQueueTab={deleteQueueTab}
            onKickQueue={kickQueue}
            onError={handleApiError}
            error={error}
            onRefresh={refreshAll}
            onToggleFiles={() => toggleRightRail('files')}
            onOpenGit={() => toggleRightRail('git')}
          />
        )}
      </main>

      {requestToDelete && (
        <DeleteQueueItemDialog
          request={requestToDelete}
          onCancel={() => setDeleteRequestId(null)}
          onConfirm={() => {
            void deleteRequest(requestToDelete.id).then(() => setDeleteRequestId(null))
          }}
        />
      )}

      {rightOpen && (
        <RightRail
          config={config}
          view={rightRailView}
          selectedProject={selectedProject}
          onOpenFile={openFile}
          onClose={() => setRightOpen(false)}
          onError={handleApiError}
        />
      )}
    </div>
  )
}

function DeleteQueueItemDialog({
  request,
  onCancel,
  onConfirm,
}: {
  request: CodexRequest
  onCancel: () => void
  onConfirm: () => void
}) {
  const label = `${request.machineName || 'request'} #${shortId(request.id)}`
  return (
    <Modal title="Move request to trash" icon={<Trash2 size={18} />} onClose={onCancel}>
      <div className="modal-body">
        <p className="muted" style={{ margin: 0 }}>
          Move <strong>{label}</strong> to trash? It will be removed from Queue and active History views until you open trash.
        </p>
        <div className="button-row">
          <GlassButton variant="ghost" onClick={onCancel}>
            Cancel
          </GlassButton>
          <GlassButton variant="danger" onClick={onConfirm}>
            <Trash2 size={13} /> Move to trash
          </GlassButton>
        </div>
      </div>
    </Modal>
  )
}

function AuthScreen({ onAuthed }: { onAuthed: () => Promise<void> }) {
  const [token, setToken] = useState(getStoredToken())
  const [error, setError] = useState('')

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setError('')
    storeToken(token)
    try {
      await api.verifyToken()
      await onAuthed()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Token was rejected.')
    }
  }

  return (
    <div className="auth-screen">
      <GlassPanel className="auth-card">
        <div className="section-header">
          <h2>API Token</h2>
          <Server size={18} />
        </div>
        <form className="form-grid" onSubmit={submit}>
          <FieldLabel label="Token">
            <GlassInput value={token} onChange={(event) => setToken(event.target.value)} type="password" autoFocus />
          </FieldLabel>
          {error && <span className="error-text">{error}</span>}
          <GlassButton variant="primary" type="submit">Unlock</GlassButton>
        </form>
      </GlassPanel>
    </div>
  )
}

function LeftSidebar({
  theme,
  onToggleTheme,
  machines,
  projects,
  requests,
  now,
  selectedProjectId,
  onSelectProject,
  onRenameProject,
  onRemoveProject,
  onChanged,
  onError,
}: {
  theme: ColorTheme
  onToggleTheme: () => void
  machines: Machine[]
  projects: Project[]
  requests: CodexRequest[]
  now: number
  selectedProjectId: string
  onSelectProject: (id: string) => void
  onRenameProject: (project: Project, name: string) => Promise<void>
  onRemoveProject: (project: Project) => Promise<boolean>
  onChanged: () => Promise<void>
  onError: (cause: unknown) => void
}) {
  const [projectModalOpen, setProjectModalOpen] = useState(false)
  const [machineModalOpen, setMachineModalOpen] = useState(false)
  const [usageModalOpen, setUsageModalOpen] = useState(false)
  const [projectDetailsId, setProjectDetailsId] = useState<string | null>(null)
  const [machineStatuses, setMachineStatuses] = useState<Record<string, { checking: boolean; success?: boolean; output: string }>>({})
  const detailProject = projects.find((project) => project.id === projectDetailsId)
  const usageLimitedRequests = useMemo(() => requests
    .filter((request) => !request.deletedAt && request.status === 'UsageLimited')
    .toSorted((left, right) => Date.parse(left.retryAfter ?? left.createdAt) - Date.parse(right.retryAfter ?? right.createdAt)),
  [requests])
  const projectQueueStates = useMemo(() => {
    const states: Record<string, { queued: number; running: number }> = {}
    for (const request of requests) {
      if (request.deletedAt || request.archivedAt || (request.status !== 'Queued' && request.status !== 'Running')) {
        continue
      }

      const state = states[request.projectId] ?? { queued: 0, running: 0 }
      if (request.status === 'Running') {
        state.running += 1
      } else {
        state.queued += 1
      }
      states[request.projectId] = state
    }

    return states
  }, [requests])
  const grouped = useMemo(
    () => machines.map((machine) => ({
      machine,
      projects: projects.filter((project) => project.machineId === machine.id),
    })),
    [machines, projects],
  )

  useEffect(() => {
    let cancelled = false

    const checkMachines = async () => {
      for (const machine of machines) {
        setMachineStatuses((current) => ({
          ...current,
          [machine.id]: { checking: true, success: current[machine.id]?.success, output: current[machine.id]?.output ?? '' },
        }))

        try {
          const result = await api.testMachine(machine.id)
          if (cancelled) return
          setMachineStatuses((current) => ({
            ...current,
            [machine.id]: { checking: false, success: result.success, output: result.output || (result.success ? 'Connection ok' : 'Connection failed') },
          }))
        } catch (cause) {
          if (cancelled) return
          setMachineStatuses((current) => ({
            ...current,
            [machine.id]: { checking: false, success: false, output: cause instanceof Error ? cause.message : 'Connection failed.' },
          }))
        }
      }
    }

    if (machines.length > 0) {
      void checkMachines()
    }

    return () => {
      cancelled = true
    }
  }, [machines])

  return (
    <aside className="sidebar">
      <div className="app-brand" aria-label="Codex Queue">
        <img src={appIconUrl} alt="" className="app-brand-icon" />
        <span className="app-brand-name">Codex Queue</span>
      </div>

      <GlassPanel>
        <div className="section-header">
          <div className="sidebar-actions">
            <GlassButton variant="ghost" size="icon" onClick={() => setProjectModalOpen(true)} title="Add project">
              <FolderPlus size={16} />
            </GlassButton>
            <GlassButton variant="ghost" size="icon" onClick={() => setUsageModalOpen(true)} title="Codex usage">
              <Gauge size={16} />
            </GlassButton>
            <GlassButton
              variant="ghost"
              size="icon"
              onClick={onToggleTheme}
              title={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}
              aria-label={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}
              aria-pressed={theme === 'dark'}
            >
              {theme === 'dark' ? <Sun size={16} /> : <Moon size={16} />}
            </GlassButton>
            <GlassButton variant="ghost" size="icon" onClick={() => setMachineModalOpen(true)} title="Manage machines">
              <Settings size={16} />
            </GlassButton>
          </div>
        </div>

        <div className="project-groups">
          {grouped.map(({ machine, projects: machineProjects }) => (
            <div key={machine.id} className="machine-group">
              <div className="machine-group-title">
                <Monitor size={14} />
                <span className="truncate">{machine.name}</span>
                <MachineConnectionStatus status={machineStatuses[machine.id]} kind={machine.kind} />
              </div>
              {machineProjects.length === 0 ? (
                <div className="empty-state">No projects</div>
              ) : (
                machineProjects.map((project) => {
                  const queueState = projectQueueStates[project.id]
                  const running = queueState?.running ?? 0
                  const queued = queueState?.queued ?? 0
                  const hasQueueActivity = running > 0 || queued > 0
                  return (
                    <div
                      key={project.id}
                      className={`project-item ${project.id === selectedProjectId ? 'active' : ''} ${running > 0 ? 'project-item--running' : hasQueueActivity ? 'project-item--queued' : ''}`}
                    >
                      <button type="button" className="project-item-main" onClick={() => onSelectProject(project.id)}>
                        <div className="project-name-row">
                          <div className="project-name truncate">{project.name}</div>
                          {hasQueueActivity && (
                            <span className={`project-queue-chip ${running > 0 ? 'running' : 'queued'}`}>
                              <span className="project-queue-dot" aria-hidden="true" />
                              {running > 0 ? `${running} running` : `${queued} queued`}
                            </span>
                          )}
                        </div>
                        <div className="meta truncate">{project.path}</div>
                      </button>
                      <button
                        type="button"
                        className="project-detail-button"
                        title="Project details"
                        onClick={() => {
                          onSelectProject(project.id)
                          setProjectDetailsId(project.id)
                        }}
                      >
                        <Settings size={14} />
                      </button>
                    </div>
                  )
                })
              )}
            </div>
          ))}
          {machines.length === 0 && <div className="empty-state">No machines configured</div>}
        </div>
      </GlassPanel>

      {projectModalOpen && (
        <ProjectModal
          machines={machines}
          onClose={() => setProjectModalOpen(false)}
          onCreated={(project) => {
            onSelectProject(project.id)
            setProjectModalOpen(false)
          }}
          onChanged={onChanged}
          onError={onError}
        />
      )}
      {usageModalOpen && (
        <UsageLimitModal
          machines={machines}
          requests={usageLimitedRequests}
          now={now}
          onClose={() => setUsageModalOpen(false)}
        />
      )}
      {machineModalOpen && (
        <MachineModal
          machines={machines}
          onClose={() => setMachineModalOpen(false)}
          onChanged={onChanged}
          onError={onError}
        />
      )}
      {detailProject && (
        <ProjectDetailsModal
          project={detailProject}
          onClose={() => setProjectDetailsId(null)}
          onRename={onRenameProject}
          onRemove={async (project) => {
            if (await onRemoveProject(project)) {
              setProjectDetailsId(null)
            }
          }}
          onError={onError}
        />
      )}
    </aside>
  )
}

function MachineConnectionStatus({
  status,
  kind,
}: {
  status?: { checking: boolean; success?: boolean; output: string }
  kind: MachineKind
}) {
  const label = status?.checking
    ? 'Checking'
    : status?.success === true
      ? 'Connected'
      : status?.success === false
        ? 'Failed'
        : 'Not checked'

  return (
    <span className="machine-status" title={status?.output || label}>
      <span className={`connection-dot ${status?.checking ? 'pending' : status?.success === true ? 'ok' : status?.success === false ? 'bad' : ''}`} />
      <span>{kind}</span>
      <span className="machine-status-label">{label}</span>
    </span>
  )
}

function UsageLimitModal({ machines, requests, now, onClose }: { machines: Machine[]; requests: CodexRequest[]; now: number; onClose: () => void }) {
  const [usage, setUsage] = useState<Record<string, MachineRateLimits>>({})
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false
    const load = () => void Promise.all(machines.map(async (machine) => {
      try {
        return await api.machineUsage(machine.id)
      } catch (cause) {
        return {
          machineId: machine.id,
          machineName: machine.name,
          available: false,
          error: cause instanceof Error ? cause.message : 'Could not read Codex usage.',
          limits: [],
        }
      }
    })).then((snapshots) => {
      if (cancelled) return
      setUsage(Object.fromEntries(snapshots.map((snapshot) => [snapshot.machineId, snapshot])))
      setLoading(false)
    })
    load()
    const refreshTimer = window.setInterval(load, 30_000)

    return () => { cancelled = true; window.clearInterval(refreshTimer) }
  }, [machines])

  return (
    <Modal title="Codex usage" icon={<Gauge size={18} />} onClose={onClose}>
      <div className="usage-modal">
        <UsageLimitSidebarPanel machines={machines} usage={usage} loading={loading} requests={requests} now={now} />
      </div>
    </Modal>
  )
}

function UsageLimitSidebarPanel({ machines, usage, loading, requests, now }: { machines: Machine[]; usage: Record<string, MachineRateLimits>; loading: boolean; requests: CodexRequest[]; now: number }) {
  const active = requests.length > 0
  const buckets = usageLimitBuckets(requests, now)

  return (
    <div className={`usage-sidebar ${active ? 'usage-sidebar--limited' : ''}`}>
      <div className="usage-sidebar-head">
        <span>Codex usage</span>
        <a className="usage-sidebar-pill" href="https://chatgpt.com/codex/settings/usage" target="_blank" rel="noreferrer">
          Open usage
        </a>
      </div>
      <div className="usage-sidebar-tip">
        <span>{active ? `${requests.length} paused by limits` : 'Live quota from Codex app-server.'}</span>
        <a href="https://community.openai.com/c/codex/37" target="_blank" rel="noreferrer">Forum</a>
      </div>
      <div className="usage-sidebar-list">
        {loading && <div className="meta">Reading current Codex limits…</div>}
        {!loading && machines.length === 0 && <div className="meta">No machines configured.</div>}
        {!loading && machines.map((machine) => (
          <MachineUsageSection key={machine.id} snapshot={usage[machine.id]} />
        ))}
        {active && <div className="usage-section-title">Paused queue requests</div>}
        {active && buckets.filter((bucket) => bucket.limited).map((bucket) => (
          <div key={bucket.key} className={`usage-sidebar-item ${bucket.limited ? 'limited' : ''} ${bucket.section ? 'usage-sidebar-item--sectioned' : ''}`}>
            {bucket.section && <div className="usage-section-title">{bucket.section}</div>}
            <div className="usage-row-head">
              <span className="truncate">{bucket.label}</span>
              <span>{bucket.percentLeft === null ? bucket.status : `${bucket.percentLeft}% left`}</span>
            </div>
            <div className={`usage-meter ${bucket.percentLeft === null ? 'unknown' : ''}`} aria-label={`${bucket.label} ${bucket.percentLeft ?? 'unknown'} percent left`}>
              <span style={{ width: `${bucket.percentLeft ?? 0}%` }} />
            </div>
            <div className="meta truncate">{bucket.message}</div>
            {bucket.detail && <div className="meta truncate">{bucket.detail}</div>}
          </div>
        ))}
      </div>
    </div>
  )
}

function MachineUsageSection({ snapshot }: { snapshot?: MachineRateLimits }) {
  if (!snapshot || !snapshot.available) {
    return <div className="usage-sidebar-item"><div className="usage-row-head"><span>{snapshot?.machineName ?? 'Machine'}</span><span>unavailable</span></div><div className="meta">{snapshot?.error ?? 'No usage data returned.'}</div></div>
  }

  return <div className="usage-sidebar-item">
    <div className="usage-section-title">{snapshot.machineName}</div>
    {snapshot.limits.map((limit) => <RateLimitSection key={limit.id} limit={limit} />)}
    {snapshot.limits.length === 0 && <div className="meta">No active rate-limit windows.</div>}
  </div>
}

function RateLimitSection({ limit }: { limit: RateLimit }) {
  const windows = [
    ['Current window', limit.primary],
    ['Secondary window', limit.secondary],
  ] as const
  return <>
    <div className="meta">{limit.name}</div>
    {windows.map(([label, window]) => window && <UsageWindow key={label} label={label} window={window} />)}
    {!limit.primary && !limit.secondary && <div className="meta">No active rate-limit windows.</div>}
    {limit.rateLimitReachedType && <div className="meta">Limit reached: {limit.rateLimitReachedType}</div>}
  </>
}

function UsageWindow({ label, window }: { label: string; window: NonNullable<RateLimit['primary']> }) {
  const remaining = Math.max(0, 100 - window.usedPercent)
  const reset = window.resetsAt ? formatDate(new Date(window.resetsAt * 1000).toISOString()) : 'reset time unknown'
  const duration = window.windowDurationMins ? ` · ${formatWindowDuration(window.windowDurationMins)}` : ''
  return <div className="usage-window">
    <div className="usage-row-head"><span>{label}{duration}</span><span>{remaining}% left</span></div>
    <div className="usage-meter" aria-label={`${label} ${remaining}% left`}><span style={{ width: `${remaining}%` }} /></div>
    <div className="meta">resets {reset}</div>
  </div>
}

function formatWindowDuration(minutes: number) {
  if (minutes % 1440 === 0) return `${minutes / 1440}d window`
  if (minutes % 60 === 0) return `${minutes / 60}h window`
  return `${minutes}m window`
}

type UsageBucket = {
  key: string
  label: string
  section?: string
  limited: boolean
  status: string
  percentLeft: number | null
  message: string
  detail?: string
}

function usageLimitBuckets(requests: CodexRequest[], now: number) {
  const limits = requests.map((request) => {
    const limitedRun = latestRunByPredicate(request.runs, (run) => run.status === 'UsageLimited')
    const model = limitedRun?.model || request.model
    const retryAfter = limitedRun?.retryAfter ?? request.retryAfter
    const reason = limitedRun?.retryReason ?? request.retryReason
    return {
      request,
      model,
      retryAfter,
      remaining: retryAfter ? formatRemainingTime(retryAfter, now) : null,
      reason,
      parsed: parseCodexUsageText(reason),
    }
  })
  const spark = limits.find((limit) => /5\.3|spark/i.test(limit.model))
  const overall = limits.find((limit) => !/5\.3|spark/i.test(limit.model)) ?? limits[0]
  const weeklyOverall = limits.find((limit) => !/5\.3|spark/i.test(limit.model) && /week|weekly/i.test(limit.reason ?? ''))
  const weeklySpark = limits.find((limit) => /5\.3|spark/i.test(limit.model) && /week|weekly/i.test(limit.reason ?? ''))

  return [
    usageLimitBucket('overall-5h', '5h limit', overall),
    usageLimitBucket('overall-weekly', 'Weekly limit', weeklyOverall),
    usageLimitBucket('spark-5h', '5h limit', spark, 'GPT-5.3-Codex-Spark limit'),
    usageLimitBucket('spark-weekly', 'Weekly limit', weeklySpark),
  ]
}

function usageLimitBucket(
  key: string,
  label: string,
  limit?: {
    request: CodexRequest
    model: string
    retryAfter?: string | null
    remaining: string | null
    reason?: string | null
    parsed: { percentLeft: number | null; resetText: string | null }
  },
  section?: string,
): UsageBucket {
  if (!limit) {
    return {
      key,
      label,
      section,
      limited: false,
      status: 'unknown',
      percentLeft: null,
      message: 'Open Codex settings for live quota.',
      detail: 'Updates here after a queue usage-limit response.',
    }
  }

  const percentLeft = limit.parsed.percentLeft ?? 0
  const resetText = limit.parsed.resetText ?? (limit.remaining ? `resets in ${limit.remaining}` : limit.retryAfter ? `resets ${formatDate(limit.retryAfter)}` : null)

  return {
    key,
    label,
    section,
    limited: true,
    status: 'paused',
    percentLeft,
    message: resetText ?? 'Reset window unknown',
    detail: `${limit.model} · ${limit.request.projectName || shortId(limit.request.id)}`,
  }
}

function parseCodexUsageText(value?: string | null) {
  const text = value ?? ''
  const percentMatch = text.match(/(\d{1,3})%\s+left/i)
  const resetMatch = text.match(/resets?\s+([^.,;\n]+)/i)
  return {
    percentLeft: percentMatch ? Math.min(100, Math.max(0, Number.parseInt(percentMatch[1], 10))) : null,
    resetText: resetMatch ? `resets ${resetMatch[1].trim()}` : null,
  }
}

function MachineModal({
  machines,
  onClose,
  onChanged,
  onError,
}: {
  machines: Machine[]
  onClose: () => void
  onChanged: () => Promise<void>
  onError: (cause: unknown) => void
}) {
  const [draft, setDraft] = useState<SaveMachineRequest>(emptyMachine)
  const [editingId, setEditingId] = useState<string | undefined>()
  const [machineToDelete, setMachineToDelete] = useState<Machine | null>(null)
  const [testResults, setTestResults] = useState<Record<string, { testing: boolean; success?: boolean; output: string; checkedAt?: string }>>({})

  const selectedMachine = machines.find((machine) => machine.id === editingId)

  const update = <K extends keyof SaveMachineRequest>(key: K, value: SaveMachineRequest[K]) => {
    setDraft((current) => ({ ...current, [key]: value }))
  }

  const edit = (machine: Machine) => {
    setEditingId(machine.id)
    setDraft({
      name: machine.name,
      kind: machine.kind,
      host: machine.host ?? '',
      port: machine.port,
      userName: machine.userName ?? '',
      sshKeyPath: machine.sshKeyPath ?? '',
      workingRoot: machine.workingRoot ?? '',
      platform: machine.platform,
    })
  }

  const reset = () => {
    setEditingId(undefined)
    setDraft(emptyMachine)
  }

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    try {
      const saved = await api.saveMachine(draft, editingId)
      setEditingId(saved.id)
      setDraft({
        name: saved.name,
        kind: saved.kind,
        host: saved.host ?? '',
        port: saved.port,
        userName: saved.userName ?? '',
        sshKeyPath: saved.sshKeyPath ?? '',
        workingRoot: saved.workingRoot ?? '',
        platform: saved.platform,
      })
      await onChanged()
    } catch (cause) {
      onError(cause)
    }
  }

  const test = async (id: string) => {
    setTestResults((current) => ({
      ...current,
      [id]: { testing: true, output: current[id]?.output ?? '' },
    }))
    try {
      const result = await api.testMachine(id)
      setTestResults((current) => ({
        ...current,
        [id]: {
          testing: false,
          success: result.success,
          output: result.output || (result.success ? 'Connection ok' : 'Connection failed'),
          checkedAt: new Date().toISOString(),
        },
      }))
    } catch (cause) {
      setTestResults((current) => ({
        ...current,
        [id]: { testing: false, success: false, output: cause instanceof Error ? cause.message : 'Connection failed.', checkedAt: new Date().toISOString() },
      }))
      onError(cause)
    }
  }

  const testAll = async () => {
    for (const machine of machines) {
      await test(machine.id)
    }
  }

  const remove = async (id: string) => {
    try {
      await api.deleteMachine(id)
      if (editingId === id) {
        reset()
      }
      await onChanged()
      setMachineToDelete(null)
    } catch (cause) {
      onError(cause)
    }
  }

  return (
    <Modal title="Machines" onClose={onClose} icon={<Monitor size={18} />} wide>
      <div className="machine-manager">
        <div className="machine-manager-list">
          <div className="machine-manager-toolbar">
            <div>
              <div className="machine-manager-title">Configured machines</div>
              <div className="meta">{machines.length} targets</div>
            </div>
            <div className="button-row">
              <GlassButton variant="secondary" size="sm" type="button" onClick={testAll} disabled={machines.length === 0}>
                <Check size={13} /> Check all
              </GlassButton>
              <GlassButton variant="primary" size="sm" type="button" onClick={reset}>
                <Plus size={13} /> New
              </GlassButton>
            </div>
          </div>

          <div className="machine-list">
            {machines.map((machine) => {
              const result = testResults[machine.id]
              return (
                <button key={machine.id} type="button" className={`machine-card ${machine.id === editingId ? 'active' : ''}`} onClick={() => edit(machine)}>
                  <div className="machine-card-main">
                    <div className="machine-card-icon">
                      {machine.kind === 'Ssh' ? <Server size={17} /> : <Monitor size={17} />}
                    </div>
                    <div className="truncate">
                      <div className="machine-name truncate">{machine.name}</div>
                      <div className="meta truncate">{machineAddress(machine)}</div>
                    </div>
                  </div>
                  <div className="machine-card-meta">
                    <span className="machine-chip">{machine.kind}</span>
                    <span className="machine-chip">{formatPlatform(machine.platform)}</span>
                    <span className={`connection-dot ${result?.testing ? 'pending' : result?.success === true ? 'ok' : result?.success === false ? 'bad' : ''}`} />
                  </div>
                </button>
              )
            })}
            {machines.length === 0 && <div className="empty-state">No machines configured</div>}
          </div>
        </div>

        <form className="machine-editor" onSubmit={submit}>
          <div className="machine-editor-head">
            <div>
              <div className="machine-manager-title">{editingId ? 'Edit machine' : 'New machine'}</div>
              <div className="meta truncate">{selectedMachine ? machineAddress(selectedMachine) : 'Create a target for queued Codex runs'}</div>
            </div>
            <div className="button-row">
              {editingId && (
                <GlassButton variant="secondary" size="sm" type="button" onClick={() => test(editingId)} disabled={testResults[editingId]?.testing}>
                  <Check size={13} /> {testResults[editingId]?.testing ? 'Checking' : 'Check'}
                </GlassButton>
              )}
              {editingId && (
                <GlassButton variant="danger" size="sm" type="button" onClick={() => setMachineToDelete(selectedMachine ?? null)}>
                  <Trash2 size={13} /> Delete
                </GlassButton>
              )}
            </div>
          </div>

          <div className="settings-section">
            <div className="settings-section-title">Identity</div>
            <div className="form-grid two">
              <FieldLabel label="Name">
                <GlassInput value={draft.name} onChange={(event) => update('name', event.target.value)} required placeholder="Local Linux" />
              </FieldLabel>
              <FieldLabel label="Kind">
                <GlassSelect value={draft.kind} onChange={(event) => update('kind', event.target.value as MachineKind)}>
                  <option value="Ssh">SSH</option>
                  <option value="Local">Local</option>
                </GlassSelect>
              </FieldLabel>
            </div>
            <div className="form-grid two">
              <FieldLabel label="Platform">
                <GlassSelect value={draft.platform ?? 'Auto'} onChange={(event) => update('platform', event.target.value as MachinePlatform)}>
                  <option value="Auto">Auto</option>
                  <option value="Linux">Linux</option>
                  <option value="MacOs">macOS</option>
                  <option value="Windows">Windows</option>
                </GlassSelect>
              </FieldLabel>
              <FieldLabel label="Working root">
                <GlassInput value={draft.workingRoot ?? ''} onChange={(event) => update('workingRoot', event.target.value)} placeholder="Blank uses the platform default" />
              </FieldLabel>
            </div>
          </div>

          {draft.kind === 'Ssh' && (
            <div className="settings-section">
              <div className="settings-section-title">Connection</div>
              <FieldLabel label="Host">
                <GlassInput value={draft.host ?? ''} onChange={(event) => update('host', event.target.value)} required placeholder="host.docker.internal" />
              </FieldLabel>
              <div className="form-grid two">
                <FieldLabel label="User">
                  <GlassInput value={draft.userName ?? ''} onChange={(event) => update('userName', event.target.value)} placeholder="jarrod" />
                </FieldLabel>
                <FieldLabel label="Port">
                  <GlassInput value={draft.port ?? 22} onChange={(event) => update('port', Number(event.target.value))} type="number" min={1} max={65535} />
                </FieldLabel>
              </div>
              <FieldLabel label="SSH key path">
                <GlassInput value={draft.sshKeyPath ?? ''} onChange={(event) => update('sshKeyPath', event.target.value)} placeholder="/home/app/.ssh/id_ed25519" />
              </FieldLabel>
            </div>
          )}

          <div className="machine-editor-actions">
            <GlassButton variant="primary" type="submit">
              <Plus size={15} /> {editingId ? 'Save machine' : 'Add machine'}
            </GlassButton>
            {editingId && <GlassButton variant="secondary" type="button" onClick={reset}>Clear</GlassButton>}
          </div>

          {editingId && testResults[editingId] && (
            <div className={`diagnostics-panel ${testResults[editingId].success === false ? 'diagnostics-panel--bad' : testResults[editingId].success ? 'diagnostics-panel--ok' : ''}`}>
              <div className="row-between">
                <strong>{testResults[editingId].testing ? 'Checking connection' : testResults[editingId].success ? 'Connection ok' : 'Connection failed'}</strong>
                {testResults[editingId].checkedAt && <span className="meta">{formatDate(testResults[editingId].checkedAt)}</span>}
              </div>
              <pre className="log-block">{testResults[editingId].output || 'Waiting for output...'}</pre>
            </div>
          )}
        </form>
      </div>
      {machineToDelete && (
        <ConfirmDialog
          title="Delete machine?"
          description={<>Delete <strong>{machineToDelete.name}</strong> from Codex Queue? Projects using this machine may no longer be available, but no files or folders will be deleted.</>}
          confirmLabel="Delete machine"
          onCancel={() => setMachineToDelete(null)}
          onConfirm={() => remove(machineToDelete.id)}
        />
      )}
    </Modal>
  )
}

function machineAddress(machine: Machine) {
  if (machine.kind === 'Local') {
    return machine.workingRoot || 'Local default root'
  }

  const user = machine.userName ? `${machine.userName}@` : ''
  return `${user}${machine.host ?? 'host'}:${machine.port}`
}

function formatPlatform(platform: MachinePlatform) {
  return platform === 'MacOs' ? 'macOS' : platform
}

function ProjectModal({
  machines,
  onClose,
  onCreated,
  onChanged,
  onError,
}: {
  machines: Machine[]
  onClose: () => void
  onCreated: (project: Project) => void
  onChanged: () => Promise<void>
  onError: (cause: unknown) => void
}) {
  const [machineId, setMachineId] = useState(machines[0]?.id ?? '')
  const [name, setName] = useState('')
  const [path, setPath] = useState('')
  const [machineCheck, setMachineCheck] = useState<{ testing: boolean; success?: boolean; output: string } | null>(null)
  const selectedMachine = machines.find((machine) => machine.id === machineId)

  useEffect(() => {
    setMachineId((current) => current || machines[0]?.id || '')
  }, [machines])

  useEffect(() => {
    setMachineCheck(null)
  }, [machineId])

  const selectFolder = (folderPath: string) => {
    setPath(folderPath)
    if (!name.trim()) {
      setName(folderPath.split(/[\\/]/).filter(Boolean).at(-1) ?? folderPath)
    }
  }

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    try {
      const project = await api.saveProject({ name, path, machineId })
      await onChanged()
      onCreated(project)
    } catch (cause) {
      onError(cause)
    }
  }

  const checkSelectedMachine = async () => {
    if (!selectedMachine) return
    setMachineCheck({ testing: true, output: '' })
    try {
      const result = await api.testMachine(selectedMachine.id)
      setMachineCheck({
        testing: false,
        success: result.success,
        output: result.output || (result.success ? 'Connection ok' : 'Connection failed'),
      })
    } catch (cause) {
      setMachineCheck({
        testing: false,
        success: false,
        output: cause instanceof Error ? cause.message : 'Connection failed.',
      })
      onError(cause)
    }
  }

  return (
    <Modal title="Add Project" onClose={onClose} icon={<FolderPlus size={18} />} wide>
      <form className="form-grid" onSubmit={submit}>
        <div className="form-grid two">
          <FieldLabel label="Machine">
            <GlassSelect value={machineId} onChange={(event) => { setMachineId(event.target.value); setPath('') }} required>
              {machines.map((machine) => (
                <option key={machine.id} value={machine.id}>{machine.name}</option>
              ))}
            </GlassSelect>
          </FieldLabel>
          <FieldLabel label="Project name">
            <GlassInput value={name} onChange={(event) => setName(event.target.value)} required placeholder="codex-queuer" />
          </FieldLabel>
        </div>
        <div className={`selected-folder ${path ? 'selected-folder--active' : ''}`}>
          <FolderOpen size={16} />
          <div className="truncate">
            <div className="selected-folder-label">Selected folder</div>
            <div className="meta truncate">{path || 'Choose a folder from the tree below'}</div>
          </div>
        </div>
        {selectedMachine && (
          <div className="folder-picker-toolbar">
            <div className="truncate">
              <div className="selected-folder-label">Machine root</div>
              <div className="meta truncate">{selectedMachine.workingRoot || 'Default working root'}</div>
            </div>
            <GlassButton variant="secondary" size="sm" type="button" onClick={checkSelectedMachine} disabled={machineCheck?.testing}>
              <Check size={13} /> {machineCheck?.testing ? 'Checking' : 'Check'}
            </GlassButton>
          </div>
        )}
        {machineCheck && !machineCheck.testing && (
          <div className={`folder-diagnostic ${machineCheck.success ? 'folder-diagnostic--ok' : 'folder-diagnostic--bad'}`}>
            <strong>{machineCheck.success ? 'Connection ok' : 'Connection failed'}</strong>
            <pre>{machineCheck.output}</pre>
          </div>
        )}
        {selectedMachine ? (
          <MachineFolderTree machine={selectedMachine} selectedPath={path} onSelect={selectFolder} onError={onError} />
        ) : (
          <div className="empty-state">Add a machine before adding projects.</div>
        )}
        <div className="row-between">
          <span className="meta truncate">{selectedMachine?.workingRoot ?? 'No working root set'}</span>
          <GlassButton variant="primary" type="submit" disabled={!machineId || !name.trim() || !path.trim()}>
            <Plus size={15} /> Add Project
          </GlassButton>
        </div>
      </form>
    </Modal>
  )
}

function MachineFolderTree({
  machine,
  selectedPath,
  onSelect,
  onError,
}: {
  machine: Machine
  selectedPath: string
  onSelect: (path: string) => void
  onError: (cause: unknown) => void
}) {
  const [entriesByPath, setEntriesByPath] = useState<Record<string, FileTreeEntry[]>>({})
  const [expanded, setExpanded] = useState<Record<string, boolean>>({ '': true })
  const [loadingPath, setLoadingPath] = useState<string | null>(null)
  const [loadError, setLoadError] = useState('')
  const root = machine.workingRoot ?? ''

  const load = useCallback(async (path = '') => {
    setLoadingPath(path)
    setLoadError('')
    try {
      const entries = await api.machineFolders(machine.id, path)
      setEntriesByPath((current) => ({ ...current, [path]: entries }))
    } catch (cause) {
      setLoadError(cause instanceof Error ? cause.message : 'Could not load folders.')
      onError(cause)
    } finally {
      setLoadingPath(null)
    }
  }, [machine.id, onError])

  useEffect(() => {
    setEntriesByPath({})
    setExpanded({ '': true })
    load('')
  }, [load, machine.id, machine.workingRoot])

  const toggle = async (entry: FileTreeEntry) => {
    setExpanded((current) => ({ ...current, [entry.path]: !current[entry.path] }))
    onSelect(entry.path)
    if (!entriesByPath[entry.path]) {
      await load(entry.path)
    }
  }

  const renderEntries = (path = '') => (
    <div className={path ? 'tree-children' : 'tree-list'}>
      {(entriesByPath[path] ?? []).map((entry) => (
        <div key={entry.path}>
          <button type="button" className={`tree-row ${entry.path === selectedPath ? 'selected' : ''}`} onClick={() => toggle(entry)}>
            {expanded[entry.path] ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
            {expanded[entry.path] ? <FolderOpen size={15} /> : <Folder size={15} />}
            <span className="truncate">{entry.name}</span>
          </button>
          {expanded[entry.path] && renderEntries(entry.path)}
        </div>
      ))}
    </div>
  )

  return (
    <div className="folder-picker">
      {root && (
        <button type="button" className={`tree-row ${root === selectedPath ? 'selected' : ''}`} onClick={() => onSelect(root)}>
          <FolderOpen size={15} />
          <span className="truncate">{root}</span>
        </button>
      )}
      {loadingPath === '' && <div className="empty-state">Loading folders...</div>}
      {loadError && <div className="empty-state empty-state--error">{loadError}</div>}
      {!loadError && loadingPath === null && entriesByPath[''] && entriesByPath[''].length === 0 && <div className="empty-state">No folders found under this working root.</div>}
      {renderEntries()}
    </div>
  )
}

function Modal({ title, icon, children, onClose, wide = false, large = false }: { title: string; icon: React.ReactNode; children: React.ReactNode; onClose: () => void; wide?: boolean; large?: boolean }) {
  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onClose()
      }
    }

    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [onClose])

  return createPortal(
    <div
      className="modal-backdrop"
      role="presentation"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) {
          onClose()
        }
      }}
    >
      <div className={`modal ${wide ? 'modal--wide' : ''} ${large ? 'modal--large' : ''}`} role="dialog" aria-modal="true" aria-label={title}>
        <div className="section-header">
          <h2>{title}</h2>
          <div className="inline-row">
            {icon}
            <GlassButton variant="ghost" size="icon" onClick={onClose} title="Close">
              <X size={16} />
            </GlassButton>
          </div>
        </div>
        <div className="modal-body">{children}</div>
      </div>
    </div>,
    document.body,
  )
}

function QueueWorkspace({
  config,
  selectedProject,
  queueTabs,
  requests,
  diagnostics,
  now,
  onCreated,
  onDiscardRequestPreview,
  onCancel,
  onResume,
  onArchiveRequest,
  onArchiveRequests,
  onUpdateRequest,
  onReorderRequests,
  onDeleteRequest,
  onUpdateProjectDefaults,
  onUpdateProjectQueueMode,
  onCreateQueueTab,
  onRenameQueueTab,
  onDeleteQueueTab,
  onKickQueue,
  onError,
  error,
  onRefresh,
  onToggleFiles,
  onOpenGit,
}: {
  config: ApiConfig
  selectedProject?: Project
  queueTabs: QueueTab[]
  requests: CodexRequest[]
  diagnostics: QueueDiagnostics | null
  now: number
  onCreated: (request: CodexRequest, replaceId?: string) => void
  onDiscardRequestPreview: (id: string) => void
  onCancel: (id: string) => Promise<void>
  onResume: (id: string) => Promise<void>
  onArchiveRequest: (id: string) => Promise<void>
  onArchiveRequests: (ids: string[]) => Promise<void>
  onUpdateRequest: (id: string, request: UpdateQueueRequest) => Promise<void>
  onReorderRequests: (projectId: string, requestIds: string[]) => Promise<void>
  onDeleteRequest: (id: string) => void
  onUpdateProjectDefaults: (project: Project, defaults: ProjectModelDefaults) => Promise<void>
  onUpdateProjectQueueMode: (project: Project, separateQueuesByTab: boolean) => Promise<void>
  onCreateQueueTab: (projectId: string, name: string) => Promise<QueueTab>
  onRenameQueueTab: (id: string, name: string) => Promise<QueueTab>
  onDeleteQueueTab: (id: string) => Promise<void>
  onKickQueue: () => Promise<void>
  onError: (cause: unknown) => void
  error: string
  onRefresh: () => Promise<void>
  onToggleFiles: () => void
  onOpenGit: () => void
}) {
  const [activeTab, setActiveTab] = useState<'queue' | 'history' | 'terminal'>('queue')
  const [activeQueueTabId, setActiveQueueTabId] = useState<string | null>(null)
  const [queueTabModal, setQueueTabModal] = useState<'create' | 'rename' | null>(null)
  const [queueTabToDelete, setQueueTabToDelete] = useState<QueueTab | null>(null)
  const [editingRequest, setEditingRequest] = useState<CodexRequest | null>(null)
  const projectQueueTabs = useMemo(() => queueTabs
    .filter((tab) => tab.projectId === selectedProject?.id)
    .toSorted((left, right) => Date.parse(left.createdAt) - Date.parse(right.createdAt)),
  [queueTabs, selectedProject?.id])
  const activeQueueTab = projectQueueTabs.find((tab) => tab.id === activeQueueTabId) ?? null
  const scopedRequests = useMemo(() => {
    if (!selectedProject) {
      return []
    }

    return requests
      .filter((request) => request.projectId === selectedProject.id)
      .toSorted((left, right) => Date.parse(left.createdAt) - Date.parse(right.createdAt))
  }, [requests, selectedProject])

  const projectQueueRequests = useMemo(() => scopedRequests
    .filter((request) => !request.deletedAt && !request.archivedAt)
    .toSorted(compareQueueRequests),
  [scopedRequests])
  const historyRequests = useMemo(() => scopedRequests
    .filter((request) => !request.deletedAt && request.status === 'Succeeded' && (request.queueTabId ?? null) === activeQueueTabId)
    .toSorted((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt)),
  [activeQueueTabId, scopedRequests])
  const queueRequests = useMemo(() => projectQueueRequests
    .filter((request) => (request.queueTabId ?? null) === activeQueueTabId)
    .toSorted(compareQueueRequests),
  [activeQueueTabId, projectQueueRequests])
  const queueNumberById = useMemo(() => new Map<string, number>(
    (selectedProject?.separateQueuesByTab ? queueRequests : projectQueueRequests)
      .map((request, index) => [request.id, index + 1]),
  ), [projectQueueRequests, queueRequests, selectedProject?.separateQueuesByTab])
  const deletedRequests = useMemo(() => scopedRequests
    .filter((request) => request.deletedAt && (request.queueTabId ?? null) === activeQueueTabId)
    .toSorted((left, right) => Date.parse(right.deletedAt ?? right.createdAt) - Date.parse(left.deletedAt ?? left.createdAt)),
  [activeQueueTabId, scopedRequests])

  useEffect(() => {
    setActiveQueueTabId(null)
    setEditingRequest(null)
    setQueueTabModal(null)
    setQueueTabToDelete(null)
  }, [selectedProject?.id])

  useEffect(() => {
    if (activeQueueTabId && !projectQueueTabs.some((tab) => tab.id === activeQueueTabId)) {
      setActiveQueueTabId(null)
    }
  }, [activeQueueTabId, projectQueueTabs])

  useEffect(() => {
    if (editingRequest && !queueRequests.some((request) => request.id === editingRequest.id && request.status === 'Queued')) {
      setEditingRequest(null)
    }
  }, [editingRequest, queueRequests])

  return (
    <div className="section-stack queue-workspace-stack">
      {selectedProject ? (
        <>
          <QueueComposer
            config={config}
            selectedProject={selectedProject}
            queueTabId={activeQueueTabId}
            requests={projectQueueRequests}
            activeTab={activeTab}
            editingRequest={editingRequest}
            error={error}
            onTabChange={setActiveTab}
            onRefresh={onRefresh}
            onToggleFiles={onToggleFiles}
            onOpenGit={onOpenGit}
            onCreated={onCreated}
            onDiscardRequestPreview={onDiscardRequestPreview}
            onUpdateRequest={onUpdateRequest}
            onCancelEdit={() => setEditingRequest(null)}
            onUpdateProjectDefaults={onUpdateProjectDefaults}
            onError={onError}
          />
          <QueueContextTabs
            tabs={projectQueueTabs}
            activeTabId={activeQueueTabId}
            separateQueuesByTab={selectedProject.separateQueuesByTab}
            queueModeChangeDisabled={projectQueueRequests.some((request) => request.status === 'Running' || request.status === 'CancelRequested')}
            onQueueModeChange={(separate) => onUpdateProjectQueueMode(selectedProject, separate)}
            onSelect={(tabId) => {
              setActiveQueueTabId(tabId)
              setEditingRequest(null)
            }}
            onCreate={() => setQueueTabModal('create')}
            onRename={(tab) => {
              setActiveQueueTabId(tab.id)
              setQueueTabModal('rename')
            }}
            onDelete={setQueueTabToDelete}
          />
          {activeTab === 'queue' ? (
            <QueueList
              requests={queueRequests}
              queueNumberById={queueNumberById}
              selectedProject={selectedProject}
              diagnostics={diagnostics}
              now={now}
              onCancel={onCancel}
              onResume={onResume}
              onArchive={onArchiveRequest}
              onArchiveAll={onArchiveRequests}
              onEdit={(request) => setEditingRequest(request)}
              onReorder={(requestIds) => {
                if (selectedProject.separateQueuesByTab) {
                  return onReorderRequests(selectedProject.id, requestIds)
                }

                const reorderedVisibleIds = [...requestIds]
                let visibleIndex = 0
                const mergedRequestIds = projectQueueRequests
                  .filter((request) => request.status === 'Queued' && !isOptimisticRequest(request.id))
                  .toSorted(compareQueueRequests)
                  .map((request) => (request.queueTabId ?? null) === activeQueueTabId
                    ? reorderedVisibleIds[visibleIndex++] ?? request.id
                    : request.id)
                return onReorderRequests(selectedProject.id, mergedRequestIds)
              }}
              onDelete={onDeleteRequest}
              onKickQueue={onKickQueue}
            />
          ) : activeTab === 'history' ? (
            <RequestHistory requests={historyRequests} deletedRequests={deletedRequests} now={now} onDelete={onDeleteRequest} />
          ) : (
            <ProjectTerminal project={selectedProject} requests={projectQueueRequests} now={now} />
          )}
          {queueTabModal && (
            <QueueTabNameModal
              mode={queueTabModal}
              initialName={queueTabModal === 'rename' ? activeQueueTab?.name ?? '' : ''}
              onCancel={() => setQueueTabModal(null)}
              onSubmit={async (name) => {
                if (queueTabModal === 'create') {
                  const created = await onCreateQueueTab(selectedProject.id, name)
                  setActiveQueueTabId(created.id)
                } else if (activeQueueTab) {
                  await onRenameQueueTab(activeQueueTab.id, name)
                }
                setQueueTabModal(null)
              }}
            />
          )}
          {queueTabToDelete && (
            <ConfirmDialog
              title="Delete tab?"
              description={<strong>{queueTabToDelete.name}</strong>}
              confirmLabel="Delete"
              onCancel={() => setQueueTabToDelete(null)}
              onConfirm={async () => {
                await onDeleteQueueTab(queueTabToDelete.id)
                setQueueTabToDelete(null)
                setActiveQueueTabId(null)
              }}
            />
          )}
        </>
      ) : (
        <GlassPanel>
          <div className="section-header">
            <h2>Select Project</h2>
            <FolderOpen size={18} />
          </div>
          <span className="muted">Choose a project from the left sidebar to add requests and view its queue.</span>
        </GlassPanel>
      )}
    </div>
  )
}

function QueueContextTabs({
  tabs,
  activeTabId,
  separateQueuesByTab,
  queueModeChangeDisabled,
  onQueueModeChange,
  onSelect,
  onCreate,
  onRename,
  onDelete,
}: {
  tabs: QueueTab[]
  activeTabId: string | null
  separateQueuesByTab: boolean
  queueModeChangeDisabled: boolean
  onQueueModeChange: (separate: boolean) => Promise<void>
  onSelect: (tabId: string | null) => void
  onCreate: () => void
  onRename: (tab: QueueTab) => void
  onDelete: (tab: QueueTab) => void
}) {
  const [savingQueueMode, setSavingQueueMode] = useState(false)
  const [contextMenu, setContextMenu] = useState<{ tab: QueueTab, x: number, y: number } | null>(null)
  const queueModeDisabled = queueModeChangeDisabled || savingQueueMode

  useEffect(() => {
    if (!contextMenu) return

    const closeMenu = () => setContextMenu(null)
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') closeMenu()
    }

    window.addEventListener('click', closeMenu)
    window.addEventListener('resize', closeMenu)
    window.addEventListener('keydown', closeOnEscape)
    return () => {
      window.removeEventListener('click', closeMenu)
      window.removeEventListener('resize', closeMenu)
      window.removeEventListener('keydown', closeOnEscape)
    }
  }, [contextMenu])

  const changeQueueMode = async (separate: boolean) => {
    setSavingQueueMode(true)
    try {
      await onQueueModeChange(separate)
    } finally {
      setSavingQueueMode(false)
    }
  }

  return (
    <div className="queue-context-toolbar">
      <div className="queue-context-tabs" role="tablist" aria-label="Queue contexts">
        <button
          type="button"
          role="tab"
          aria-label="Base queue"
          aria-selected={activeTabId === null}
          className={activeTabId === null ? 'active' : ''}
          onClick={() => onSelect(null)}
        >
          <ClipboardList size={15} />
        </button>
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            role="tab"
            aria-selected={activeTabId === tab.id}
            className={activeTabId === tab.id ? 'active' : ''}
            onClick={() => onSelect(tab.id)}
            onContextMenu={(event) => {
              event.preventDefault()
              onSelect(tab.id)
              setContextMenu({
                tab,
                x: Math.max(8, Math.min(event.clientX, window.innerWidth - 172)),
                y: Math.max(8, Math.min(event.clientY, window.innerHeight - 92)),
              })
            }}
          >
            <span className="truncate">{tab.name}</span>
          </button>
        ))}
        <button type="button" aria-label="Create tab" title="Create tab" onClick={onCreate}>
          <Plus size={15} />
        </button>
      </div>
      <label
        className={`queue-mode-toggle ${separateQueuesByTab ? 'active' : ''} ${queueModeDisabled ? 'disabled' : ''}`}
        title={queueModeChangeDisabled
          ? 'Finish or cancel running requests before changing queue mode.'
          : separateQueuesByTab
            ? 'Each tab runs and numbers its own queue independently.'
            : 'All tabs run in one project-wide queue order.'}
      >
        <input
          type="checkbox"
          checked={separateQueuesByTab}
          disabled={queueModeDisabled}
          onChange={(event) => { void changeQueueMode(event.target.checked).catch(() => undefined) }}
        />
        <span className="queue-mode-toggle-icon"><Check size={11} /></span>
        <span>{savingQueueMode ? 'Saving…' : 'Separate queues'}</span>
      </label>
      {contextMenu && createPortal(
        <div className="queue-tab-context-menu" role="menu" aria-label={`${contextMenu.tab.name} tab actions`} style={{ left: contextMenu.x, top: contextMenu.y }}>
          <button type="button" role="menuitem" onClick={() => onRename(contextMenu.tab)}>
            <Pencil size={14} /> Rename tab
          </button>
          <button type="button" role="menuitem" className="danger" onClick={() => onDelete(contextMenu.tab)}>
            <Trash2 size={14} /> Delete tab
          </button>
        </div>,
        document.body,
      )}
    </div>
  )
}

function QueueTabNameModal({
  mode,
  initialName,
  onCancel,
  onSubmit,
}: {
  mode: 'create' | 'rename'
  initialName: string
  onCancel: () => void
  onSubmit: (name: string) => Promise<void>
}) {
  const [name, setName] = useState(initialName)
  const [saving, setSaving] = useState(false)
  const [submitError, setSubmitError] = useState('')

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    const trimmedName = name.trim()
    if (!trimmedName || saving) return

    setSubmitError('')
    setSaving(true)
    try {
      await onSubmit(trimmedName)
    } catch (cause) {
      setSubmitError(cause instanceof Error ? cause.message : 'Request failed.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal title={mode === 'create' ? 'New tab' : 'Rename tab'} icon={<Pencil size={18} />} onClose={onCancel}>
      <form className="form-grid" onSubmit={(event) => void submit(event)}>
        <GlassInput
          value={name}
          onChange={(event) => setName(event.target.value)}
          autoFocus
          required
          maxLength={80}
          aria-label="Tab name"
        />
        {submitError && <span className="error-text">{submitError}</span>}
        <div className="button-row modal-form-actions">
          <GlassButton variant="ghost" type="button" onClick={onCancel} disabled={saving}>Cancel</GlassButton>
          <GlassButton variant="primary" type="submit" disabled={saving || !name.trim() || (mode === 'rename' && name.trim() === initialName)}>
            {mode === 'create' ? <Plus size={14} /> : <Check size={14} />}
            {mode === 'create' ? 'Create' : 'Rename'}
          </GlassButton>
        </div>
      </form>
    </Modal>
  )
}

function ProjectDetailsModal({
  project,
  onClose,
  onRename,
  onRemove,
  onError,
}: {
  project: Project
  onClose: () => void
  onRename: (project: Project, name: string) => Promise<void>
  onRemove: (project: Project) => Promise<void>
  onError: (cause: unknown) => void
}) {
  const [name, setName] = useState(project.name)
  const [saving, setSaving] = useState(false)
  const [confirmingRemove, setConfirmingRemove] = useState(false)

  useEffect(() => {
    setName(project.name)
    setSaving(false)
  }, [project.id, project.name])

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    const trimmed = name.trim()
    if (!trimmed || trimmed === project.name) {
      setName(project.name)
      return
    }

    setSaving(true)
    try {
      await onRename(project, trimmed)
    } catch (cause) {
      onError(cause)
    } finally {
      setSaving(false)
    }
  }

  return (
    <>
      <Modal title="Project Details" onClose={onClose} icon={<FolderOpen size={18} />}>
        <form className="form-grid" onSubmit={submit}>
        <FieldLabel label="Project name">
          <div className="project-name-form">
            <GlassInput value={name} onChange={(event) => setName(event.target.value)} autoFocus required />
            <GlassButton variant="primary" size="icon" type="submit" disabled={saving || !name.trim() || name.trim() === project.name} title="Save project name">
              <Check size={15} />
            </GlassButton>
          </div>
        </FieldLabel>

        <div className="project-detail-summary">
          <div>
            <div className="selected-folder-label">Folder</div>
            <div className="meta truncate">{project.path}</div>
          </div>
          <span className="machine-chip">{project.machineName}</span>
        </div>

        <div className="project-detail-summary">
          <div>
            <div className="selected-folder-label">Default request model</div>
            <div className="meta truncate">{formatModel(project.defaultModel || 'App default', project.defaultModelEffort, project.defaultModelSpeed)}</div>
          </div>
          <div>
            <div className="selected-folder-label">Default commit model</div>
            <div className="meta truncate">{formatModel(project.defaultCommitModel || 'App default', project.defaultCommitModelEffort, project.defaultCommitModelSpeed)}</div>
          </div>
        </div>

        <div className="danger-zone">
          <div className="truncate">
            <div className="selected-folder-label">Remove from app</div>
            <div className="meta truncate">Project files and folders stay on disk.</div>
          </div>
          <GlassButton variant="danger" type="button" onClick={() => setConfirmingRemove(true)}>
            <Trash2 size={15} /> Remove
          </GlassButton>
        </div>
        </form>
      </Modal>
      {confirmingRemove && (
        <ConfirmDialog
          title="Remove project?"
          description={<>Remove <strong>{project.name}</strong> from Codex Queue? This cancels active queue work and removes its history from this web app, but does not delete files or folders from disk.</>}
          confirmLabel="Remove project"
          onCancel={() => setConfirmingRemove(false)}
          onConfirm={() => onRemove(project)}
        />
      )}
    </>
  )
}

function QueueComposer({
  config,
  selectedProject,
  queueTabId,
  requests,
  activeTab,
  editingRequest,
  error,
  onTabChange,
  onRefresh,
  onToggleFiles,
  onOpenGit,
  onCreated,
  onDiscardRequestPreview,
  onUpdateRequest,
  onCancelEdit,
  onUpdateProjectDefaults,
  onError,
}: {
  config: ApiConfig
  selectedProject: Project
  queueTabId: string | null
  requests: CodexRequest[]
  activeTab: 'queue' | 'history' | 'terminal'
  editingRequest: CodexRequest | null
  error: string
  onTabChange: (tab: 'queue' | 'history' | 'terminal') => void
  onRefresh: () => Promise<void>
  onToggleFiles: () => void
  onOpenGit: () => void
  onCreated: (request: CodexRequest, replaceId?: string) => void
  onDiscardRequestPreview: (id: string) => void
  onUpdateRequest: (id: string, request: UpdateQueueRequest) => Promise<void>
  onCancelEdit: () => void
  onUpdateProjectDefaults: (project: Project, defaults: ProjectModelDefaults) => Promise<void>
  onError: (cause: unknown) => void
}) {
  const defaults = useMemo(() => projectModelDefaults(selectedProject, config.models), [config.models, selectedProject])
  const [requestModel, setRequestModel] = useState<ModelValue>(defaults.requestModel)
  const [commitModel, setCommitModel] = useState<ModelValue>(defaults.commitModel)
  const [generateCommit, setGenerateCommit] = useState(defaults.generateCommit)
  const [separateCommitSession, setSeparateCommitSession] = useState(defaults.separateCommitSession)
  const [prompt, setPrompt] = useState('')
  const [attachments, setAttachments] = useState<LocalQueueAttachment[]>([])
  const [attachmentError, setAttachmentError] = useState('')
  const [draggingFiles, setDraggingFiles] = useState(false)
  const [savingDefaults, setSavingDefaults] = useState(false)
  const [isQueueing, setIsQueueing] = useState(false)
  const fileInputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (editingRequest) {
      setRequestModel({ model: editingRequest.model, effort: editingRequest.modelEffort || 'medium', speed: editingRequest.modelSpeed || 'normal' })
      setCommitModel({
        model: editingRequest.commitModel || defaults.commitModel.model,
        effort: editingRequest.commitModelEffort || defaults.commitModel.effort,
        speed: editingRequest.commitModelSpeed || defaults.commitModel.speed,
      })
      setGenerateCommit(editingRequest.generateCommit)
      setSeparateCommitSession(editingRequest.separateCommitSession)
      setPrompt(editingRequest.prompt)
      setAttachments([])
      setAttachmentError('')
      return
    }

    setRequestModel(defaults.requestModel)
    setCommitModel(defaults.commitModel)
    setGenerateCommit(defaults.generateCommit)
    setSeparateCommitSession(defaults.separateCommitSession)
  }, [defaults, editingRequest, selectedProject.id])

  const defaultsChanged =
    requestModel.model !== defaults.requestModel.model ||
    requestModel.effort !== defaults.requestModel.effort ||
    requestModel.speed !== defaults.requestModel.speed ||
    commitModel.model !== defaults.commitModel.model ||
    commitModel.effort !== defaults.commitModel.effort ||
    commitModel.speed !== defaults.commitModel.speed ||
    generateCommit !== defaults.generateCommit ||
    separateCommitSession !== defaults.separateCommitSession

  const resetModelSelections = () => {
    setRequestModel(defaults.requestModel)
    setCommitModel(defaults.commitModel)
    setGenerateCommit(defaults.generateCommit)
    setSeparateCommitSession(defaults.separateCommitSession)
  }

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (!editingRequest) {
      requestCompletionNotificationPermission()
    }
    setIsQueueing(true)
    let previewId: string | null = null
    try {
      const payload: UpdateQueueRequest = {
        prompt,
        attachments: editingRequest && attachments.length === 0 ? undefined : attachmentPayload(attachments),
        model: requestModel.model,
        modelEffort: requestModel.effort,
        modelSpeed: requestModel.speed,
        generateCommit,
        separateCommitSession: generateCommit && separateCommitSession,
        commitModel: commitModel.model,
        commitModelEffort: commitModel.effort,
        commitModelSpeed: commitModel.speed,
      }

      if (editingRequest) {
        await onUpdateRequest(editingRequest.id, payload)
        onCancelEdit()
      } else {
        previewId = `optimistic:${selectedProject.id}:${Date.now()}`
        const nextQueueOrder = requests
          .filter((item) => item.projectId === selectedProject.id && !item.deletedAt && !item.archivedAt)
          .reduce<number>((maxOrder, item) => Math.max(maxOrder, item.queueOrder), 0) + 1
        const createdAt = new Date().toISOString()
        const previewRequest: CodexRequest = {
          id: previewId,
          projectId: selectedProject.id,
          queueTabId,
          projectName: selectedProject.name,
          projectPath: selectedProject.path,
          machineId: selectedProject.machineId,
          machineName: selectedProject.machineName,
          machineKind: selectedProject.machineKind,
          prompt,
          attachments: attachmentPayload(attachments).map(({ name, contentType, size }) => ({ name, contentType, size })),
          model: requestModel.model,
          modelEffort: requestModel.effort,
          modelSpeed: requestModel.speed,
          queueOrder: nextQueueOrder,
          status: 'Queued',
          generateCommit,
          separateCommitSession: generateCommit && separateCommitSession,
          commitModel: commitModel.model,
          commitModelEffort: commitModel.effort,
          commitModelSpeed: commitModel.speed,
          createdAt,
          runs: [],
        }
        onTabChange('queue')
        onCreated(previewRequest)
        setPrompt('')
        setAttachments([])
        setAttachmentError('')
        resetModelSelections()

        const createdRequest = await api.createRequest({
          projectId: selectedProject.id,
          queueTabId,
          ...payload,
          attachments: attachmentPayload(attachments),
        })
        onCreated(createdRequest, previewId)
        return
      }

      setPrompt('')
      setAttachments([])
      setAttachmentError('')
      resetModelSelections()
    } catch (cause) {
      if (previewId) {
        onDiscardRequestPreview(previewId)
      }
      onError(cause)
    } finally {
      setIsQueueing(false)
    }
  }

  const addFiles = async (files: FileList | File[]) => {
    setAttachmentError('')
    try {
      const normalizedFiles = Array.from(files).map(normalizeAttachmentFile)
      const nextAttachments = await Promise.all(normalizedFiles.map(readQueueAttachment))
      setAttachments((current) => {
        const merged = [...current, ...nextAttachments]
        if (merged.length > 8) {
          setAttachmentError('Attach up to 8 files per request.')
          return merged.slice(0, 8)
        }

        return merged
      })
    } catch (cause) {
      setAttachmentError(cause instanceof Error ? cause.message : 'Could not read attachment.')
    }
  }

  const dropFiles = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault()
    setDraggingFiles(false)
    if (event.dataTransfer.files.length > 0) {
      void addFiles(event.dataTransfer.files)
    }
  }

  const pasteFiles = (event: ClipboardEvent<HTMLDivElement>) => {
    const files = Array.from(event.clipboardData.files)
    const itemFiles = Array.from(event.clipboardData.items)
      .filter((item) => item.kind === 'file')
      .map((item) => item.getAsFile())
      .filter((file): file is File => Boolean(file))
    const pastedFiles = files.length > 0 ? files : itemFiles
    if (pastedFiles.length === 0) {
      return
    }

    event.preventDefault()
    void addFiles(pastedFiles)
  }

  const saveDefaults = async () => {
    setSavingDefaults(true)
    try {
      await onUpdateProjectDefaults(selectedProject, { requestModel, commitModel, generateCommit, separateCommitSession: generateCommit && separateCommitSession })
    } catch (cause) {
      onError(cause)
    } finally {
      setSavingDefaults(false)
    }
  }

  return (
    <GlassPanel className="prompt-card">
      <div className="prompt-card-header">
        <div className="workspace-tabs" role="tablist" aria-label="Workspace views">
          <button type="button" className={activeTab === 'queue' ? 'active' : ''} onClick={() => onTabChange('queue')}>
            <ClipboardList size={15} /> Queue
          </button>
          <button type="button" className={activeTab === 'history' ? 'active' : ''} onClick={() => onTabChange('history')}>
            <History size={15} /> History
          </button>
          <button type="button" className={activeTab === 'terminal' ? 'active' : ''} onClick={() => onTabChange('terminal')}>
            <TerminalIcon size={15} /> Terminal
          </button>
        </div>
        <div className="workspace-actions">
          {error && <span className="error-text truncate">{error}</span>}
          <GlassButton variant="ghost" size="icon" onClick={onRefresh} title="Refresh">
            <RefreshCcw size={17} />
          </GlassButton>
          <GlassButton variant="secondary" size="icon" onClick={onOpenGit} title="Open git panel">
            <GitBranch size={17} />
          </GlassButton>
          <GlassButton variant="secondary" size="icon" onClick={onToggleFiles} title="Toggle project files">
            <Menu size={18} />
          </GlassButton>
        </div>
      </div>
      <form className="composer-form" onSubmit={submit}>
        <FieldLabel label="Prompt">
          {editingRequest && (
            <div className="edit-request-banner">
              <span>Editing queued request {shortId(editingRequest.id)}</span>
              <GlassButton variant="ghost" size="sm" type="button" onClick={onCancelEdit}>
                <X size={13} /> Cancel edit
              </GlassButton>
            </div>
          )}
          <div
            className={`prompt-dropzone ${draggingFiles ? 'dragging' : ''}`}
            onDragOver={(event) => {
              event.preventDefault()
              setDraggingFiles(true)
            }}
            onDragLeave={() => setDraggingFiles(false)}
            onDrop={dropFiles}
            onPaste={pasteFiles}
          >
            <GlassTextarea value={prompt} onChange={(event) => setPrompt(event.target.value)} required placeholder="Describe the change Codex should make in the selected project." />
            <div className="attachment-row">
              <input
                ref={fileInputRef}
                type="file"
                multiple
                className="sr-only"
                onChange={(event) => {
                  if (event.target.files) {
                    void addFiles(event.target.files)
                  }
                  event.target.value = ''
                }}
              />
              <GlassButton variant="secondary" size="sm" type="button" onClick={() => fileInputRef.current?.click()}>
                <Plus size={13} /> Attach files
              </GlassButton>
              <span className="meta">Drag, attach, or paste files/images. Images pass to Codex CLI; text/code/CSV include previews.</span>
            </div>
            {editingRequest && editingRequest.attachments.length > 0 && attachments.length === 0 && (
              <div className="existing-attachment-summary">
                <span className="meta">Existing attachments are kept unless you attach replacement files.</span>
                <AttachmentMetadataChips attachments={editingRequest.attachments} />
              </div>
            )}
            {attachments.length > 0 && (
              <AttachmentPreviewList
                attachments={attachments}
                onRemove={(index) => setAttachments((current) => current.filter((_, itemIndex) => itemIndex !== index))}
              />
            )}
            {attachmentError && <span className="error-text">{attachmentError}</span>}
          </div>
        </FieldLabel>
        <div className="composer-grid compact">
          <ModelPicker label="Request" options={config.models} value={requestModel} onChange={setRequestModel} />
          <ModelPicker label="Commit" options={config.models} value={commitModel} onChange={setCommitModel} disabled={!generateCommit || !separateCommitSession} />
        </div>
        <div className="composer-actions-row">
          <div className="commit-options">
            <div className="commit-toggle-group" aria-label="Commit options">
              <label className={`commit-toggle ${generateCommit ? 'active' : ''}`}>
                <input
                  type="checkbox"
                  checked={generateCommit}
                  onChange={(event) => {
                    setGenerateCommit(event.target.checked)
                    if (!event.target.checked) {
                      setSeparateCommitSession(false)
                    }
                  }}
                />
                <span className="commit-toggle-icon"><Check size={12} /></span>
                <span>Generate git commit</span>
              </label>
              <label className={`commit-toggle ${generateCommit && separateCommitSession ? 'active' : ''} ${!generateCommit ? 'disabled' : ''}`}>
                <input
                  type="checkbox"
                  checked={generateCommit && separateCommitSession}
                  disabled={!generateCommit}
                  onChange={(event) => setSeparateCommitSession(event.target.checked)}
                />
                <span className="commit-toggle-icon"><Check size={12} /></span>
                <span>Separate commit session</span>
              </label>
            </div>
          </div>
          <div className="button-row">
            <GlassButton variant="secondary" size="sm" type="button" onClick={saveDefaults} disabled={!defaultsChanged || savingDefaults}>
              <Check size={13} /> {savingDefaults ? 'Saving' : 'Save defaults'}
            </GlassButton>
            <GlassButton className="queue-submit-button" variant="primary" type="submit" disabled={!prompt.trim() || !requestModel.model.trim() || isQueueing}>
              {isQueueing ? <RefreshCcw size={16} className="action-spinner" /> : <Play size={16} />}
              {isQueueing ? (editingRequest ? 'Updating...' : 'Queueing...') : (editingRequest ? 'Update' : 'Queue')}
            </GlassButton>
          </div>
        </div>
      </form>
    </GlassPanel>
  )
}

function AttachmentPreviewList({
  attachments,
  onRemove,
}: {
  attachments: LocalQueueAttachment[]
  onRemove: (index: number) => void
}) {
  const textLikeCount = attachments.filter((attachment) => attachment.insight.preview).length
  const imageCount = attachments.filter((attachment) => attachment.insight.kind === 'image').length

  return (
    <div className="attachment-preview-stack" aria-label="Attachment previews">
      <div className="attachment-preview-summary">
        <span>{attachments.length} {attachments.length === 1 ? 'file' : 'files'}</span>
        {textLikeCount > 0 && <span>{textLikeCount} extracted</span>}
        {imageCount > 0 && <span>{imageCount} image{imageCount === 1 ? '' : 's'}</span>}
      </div>
      <div className="attachment-preview-grid">
        {attachments.map((attachment, index) => (
          <AttachmentPreviewCard
            key={`${attachment.name}:${index}`}
            attachment={attachment}
            onRemove={() => onRemove(index)}
          />
        ))}
      </div>
    </div>
  )
}

function AttachmentPreviewCard({
  attachment,
  onRemove,
}: {
  attachment: LocalQueueAttachment
  onRemove: () => void
}) {
  const icon = attachment.insight.kind === 'image' ? <ImageIcon size={15} /> : <FileText size={15} />

  return (
    <article className={`attachment-preview-card attachment-preview-card--${attachment.insight.kind}`}>
      <div className="attachment-preview-head">
        <span className="attachment-kind-icon" aria-hidden="true">{icon}</span>
        <div className="attachment-preview-title">
          <strong className="truncate" title={attachment.name}>{attachment.name}</strong>
          <span>{attachment.insight.label} · {formatBytes(attachment.size)}</span>
        </div>
        <button type="button" onClick={onRemove} aria-label={`Remove ${attachment.name}`}>
          <X size={13} />
        </button>
      </div>
      {attachment.insight.detail && <div className="attachment-preview-detail">{attachment.insight.detail}</div>}
      {attachment.insight.imageUrl && (
        <img className="attachment-image-preview" src={attachment.insight.imageUrl} alt={attachment.name} />
      )}
      {attachment.insight.preview && (
        <pre className="attachment-text-preview">{attachment.insight.preview}</pre>
      )}
    </article>
  )
}

function AttachmentMetadataChips({ attachments }: { attachments: Array<{ name: string, contentType: string, size: number }> }) {
  if (attachments.length === 0) {
    return null
  }

  return (
    <div className="request-attachments">
      {attachments.map((attachment, index) => {
        const kind = attachmentKindFromMetadata(attachment)
        return (
          <span key={`${attachment.name}:${index}`} className={`model-chip attachment-meta-chip attachment-meta-chip--${kind}`} title={`${attachment.contentType} · ${formatBytes(attachment.size)}`}>
            {kind === 'image' ? <ImageIcon size={12} /> : <FileText size={12} />}
            <span className="truncate">{attachment.name}</span>
            <span>{formatBytes(attachment.size)}</span>
          </span>
        )
      })}
    </div>
  )
}

function attachmentKindFromMetadata(attachment: { name: string, contentType: string }) {
  if (attachment.contentType.startsWith('image/')) return 'image'
  if (isJsonAttachment(attachment.name, attachment.contentType)) return 'json'
  if (isCsvAttachment(attachment.name, attachment.contentType)) return 'csv'
  if (isTextLikeAttachment(attachment.name, attachment.contentType)) return 'text'
  return 'binary'
}

function ModelPicker({
  label,
  options,
  value,
  onChange,
  disabled = false,
}: {
  label: string
  options: ModelOption[]
  value: ModelValue
  onChange: (value: ModelValue) => void
  disabled?: boolean
}) {
  const selectedOption = options.find((option) => option.model === value.model)
  const supportsPriority = selectedOption?.supportsPriority ?? false
  const supportsUltra = supportsUltraEffort(value.model)
  const selectedEffort = supportsUltra || value.effort !== 'ultra' ? value.effort : 'xhigh'
  const selectedModelValue = selectedOption ? value.model : 'custom'
  const dropdownOptions = useMemo(
    () => [...options.map((option) => ({ label: option.label, value: option.model })), { label: 'Custom', value: 'custom' }],
    [options],
  )

  useEffect(() => {
    if (!supportsUltra && value.effort === 'ultra') {
      onChange({ ...value, effort: 'xhigh' })
    }
  }, [onChange, supportsUltra, value])

  return (
    <div className={`model-picker-grid ${disabled ? 'model-picker-grid--disabled' : ''}`} aria-disabled={disabled}>
      <div className="model-picker-head">
        <span className="model-picker-title">{label}</span>
        <GlassDropdownSelect
          label={`${label} model`}
          options={dropdownOptions}
          value={selectedModelValue}
          disabled={disabled}
          onChange={(nextValue) => {
            if (nextValue === 'custom') {
              onChange({ ...value, model: value.model || '' })
              return
            }
            const option = options.find((item) => item.model === nextValue)
            if (option) {
              onChange({ model: option.model, effort: value.effort || 'medium', speed: option.supportsPriority ? value.speed : 'normal' })
            }
          }}
        />
      </div>
      {!selectedOption && (
        <GlassInput value={value.model} disabled={disabled} onChange={(event) => onChange({ ...value, model: event.target.value })} placeholder="model id" />
      )}
      <div className={`model-options-row ${supportsPriority ? '' : 'model-options-row--single'}`}>
        <SegmentedRadio
          label="Effort"
          name={`${label}-effort`}
          value={selectedEffort}
          disabled={disabled}
          options={[
            { label: 'Light', value: 'low' },
            { label: 'Medium', value: 'medium' },
            { label: 'High', value: 'high' },
            { label: 'XHigh', value: 'xhigh' },
            ...(supportsUltra ? [{ label: 'Ultra', value: 'ultra' }] : []),
          ]}
          onChange={(effort) => onChange({ ...value, effort })}
        />
        {supportsPriority && (
          <SegmentedRadio
            label="Speed"
            name={`${label}-speed`}
            value={value.speed}
            disabled={disabled}
            options={[
              { label: 'Normal', value: 'normal' },
              { label: 'x1.5', value: 'priority' },
            ]}
            onChange={(speed) => onChange({ ...value, speed })}
          />
        )}
      </div>
    </div>
  )
}

function supportsUltraEffort(model: string) {
  return /^gpt-5\.6(?:$|[-.])/i.test(model.trim())
}

function SegmentedRadio({
  label,
  name,
  value,
  options,
  disabled = false,
  onChange,
}: {
  label: string
  name: string
  value: string
  options: Array<{ label: string; value: string }>
  disabled?: boolean
  onChange: (value: string) => void
}) {
  return (
    <div className="segmented-field" aria-label={label}>
      <span>{label}</span>
      <div className="segmented-radio">
        {options.map((option) => (
          <label key={option.value} className={`segmented-radio-option--${option.value} ${option.value === value ? 'active' : ''}`}>
            <input
              type="radio"
              name={name}
              value={option.value}
              checked={option.value === value}
              disabled={disabled}
              onChange={() => onChange(option.value)}
            />
            {option.label}
          </label>
        ))}
      </div>
    </div>
  )
}

function useRequestDetails(summary?: CodexRequest) {
  const [details, setDetails] = useState<CodexRequest | undefined>()
  const requestId = summary?.id
  const requestStatus = summary?.status

  useEffect(() => {
    if (!requestId || isOptimisticRequest(requestId)) {
      setDetails(undefined)
      return
    }

    let cancelled = false
    const load = () => {
      void api.request(requestId)
        .then((request) => {
          if (!cancelled) setDetails(request)
        })
        .catch(() => undefined)
    }

    load()
    const active = requestStatus === 'Running' || requestStatus === 'CancelRequested' || requestStatus === 'UsageLimited'
    const timer = active ? window.setInterval(load, 2200) : undefined
    return () => {
      cancelled = true
      if (timer !== undefined) window.clearInterval(timer)
    }
  }, [requestId, requestStatus])

  return details?.id === summary?.id ? details : summary
}

function QueueList({
  requests,
  queueNumberById,
  selectedProject,
  diagnostics,
  now,
  onCancel,
  onResume,
  onArchive,
  onArchiveAll,
  onEdit,
  onReorder,
  onDelete,
  onKickQueue,
}: {
  requests: CodexRequest[]
  queueNumberById: Map<string, number>
  selectedProject?: Project
  diagnostics: QueueDiagnostics | null
  now: number
  onCancel: (id: string) => Promise<void>
  onResume: (id: string) => Promise<void>
  onArchive: (id: string) => Promise<void>
  onArchiveAll: (ids: string[]) => Promise<void>
  onEdit: (request: CodexRequest) => void
  onReorder: (requestIds: string[]) => Promise<void>
  onDelete: (id: string) => void
  onKickQueue: () => Promise<void>
}) {
  const [selectedRequestId, setSelectedRequestId] = useState<string | null>(null)
  const [draggedRequestId, setDraggedRequestId] = useState<string | null>(null)
  const [dropTargetId, setDropTargetId] = useState<string | null>(null)
  const [reorderingRequestId, setReorderingRequestId] = useState<string | null>(null)
  const mobileDragReorder = useMediaQuery('(max-width: 820px)')
  const selectedRequestSummary = requests.find((request) => request.id === selectedRequestId) ?? requests[0]
  const selectedRequest = useRequestDetails(selectedRequestSummary)
  const queuedRequestIds = useMemo(
    () => requests
      .filter((request) => request.status === 'Queued' && !isOptimisticRequest(request.id))
      .map((request) => request.id),
    [requests],
  )
  const queuedPositionById = useMemo(
    () => new Map(queuedRequestIds.map((id, index) => [id, index])),
    [queuedRequestIds],
  )
  const succeededRequestIds = requests
    .filter((request) => request.status === 'Succeeded' && !request.archivedAt && !request.deletedAt)
    .map((request) => request.id)

  useEffect(() => {
    if (requests.length === 0) {
      setSelectedRequestId(null)
      return
    }

    if (!selectedRequestId || !requests.some((request) => request.id === selectedRequestId)) {
      setSelectedRequestId(requests[0].id)
    }
  }, [requests, selectedRequestId])

  const submitQueuedOrder = async (requestIds: string[], movedRequestId: string) => {
    setReorderingRequestId(movedRequestId)
    try {
      await onReorder(requestIds)
    } finally {
      setReorderingRequestId(null)
    }
  }

  const moveQueuedRequest = async (requestId: string, direction: -1 | 1) => {
    const fromIndex = queuedRequestIds.indexOf(requestId)
    const toIndex = fromIndex + direction
    if (fromIndex === -1 || toIndex < 0 || toIndex >= queuedRequestIds.length || reorderingRequestId) {
      return
    }

    const nextIds = [...queuedRequestIds]
    const [movedId] = nextIds.splice(fromIndex, 1)
    nextIds.splice(toIndex, 0, movedId)
    await submitQueuedOrder(nextIds, requestId)
  }

  const reorderQueuedRequests = async (targetRequestId: string) => {
    if (!draggedRequestId || draggedRequestId === targetRequestId) {
      setDraggedRequestId(null)
      setDropTargetId(null)
      return
    }

    const fromIndex = queuedRequestIds.indexOf(draggedRequestId)
    const toIndex = queuedRequestIds.indexOf(targetRequestId)
    if (fromIndex === -1 || toIndex === -1) {
      setDraggedRequestId(null)
      setDropTargetId(null)
      return
    }

    const nextIds = [...queuedRequestIds]
    const [movedId] = nextIds.splice(fromIndex, 1)
    nextIds.splice(toIndex, 0, movedId)
    setDraggedRequestId(null)
    setDropTargetId(null)
    await submitQueuedOrder(nextIds, draggedRequestId)
  }

  return (
    <GlassPanel className="queue-panel">
      <div className="section-header">
        <h2>{selectedProject ? `${selectedProject.name} Queue` : 'Queue'}</h2>
        <div className="queue-header-actions">
          <span className="meta">{requests.length} requests</span>
          <QueueKickButton diagnostics={diagnostics} onKickQueue={onKickQueue} />
          {succeededRequestIds.length > 0 && (
            <GlassButton className="done-all-button" variant="primary" size="sm" type="button" onClick={() => { void onArchiveAll(succeededRequestIds) }}>
              <Check size={13} /> Done all
            </GlassButton>
          )}
        </div>
      </div>
      <div className="queue-workbench">
        <div className="request-list" aria-label="Queued requests">
          {requests.length === 0 && <span className="muted">No queued requests yet.</span>}
          {requests.map((request, index) => (
            <RequestCard
              key={request.id}
              request={request}
              now={now}
              queueNumber={queueNumberById.get(request.id) ?? index + 1}
              selected={request.id === selectedRequest?.id}
              onSelect={() => setSelectedRequestId(request.id)}
              onCancel={onCancel}
              onResume={onResume}
              onArchive={onArchive}
              onEdit={onEdit}
              onDelete={onDelete}
              canMoveUp={(queuedPositionById.get(request.id) ?? -1) > 0}
              canMoveDown={(queuedPositionById.get(request.id) ?? queuedRequestIds.length) < queuedRequestIds.length - 1}
              reorderDisabled={reorderingRequestId !== null}
              canDragReorder={mobileDragReorder && request.status === 'Queued' && !isOptimisticRequest(request.id) && reorderingRequestId === null}
              onMoveUp={() => { void moveQueuedRequest(request.id, -1).catch(() => undefined) }}
              onMoveDown={() => { void moveQueuedRequest(request.id, 1).catch(() => undefined) }}
              dragging={request.id === draggedRequestId}
              dragOver={request.id === dropTargetId}
              onDragStart={(event) => {
                if (request.status === 'Queued' && mobileDragReorder && !reorderingRequestId) {
                  event.dataTransfer.effectAllowed = 'move'
                  event.dataTransfer.setData('text/plain', request.id)
                  setDraggedRequestId(request.id)
                }
              }}
              onDragOver={(event) => {
                if (draggedRequestId && request.status === 'Queued' && mobileDragReorder) {
                  event.preventDefault()
                  setDropTargetId(request.id)
                }
              }}
              onDrop={() => {
                if (request.status === 'Queued' && mobileDragReorder) {
                  void reorderQueuedRequests(request.id).catch(() => undefined)
                }
              }}
              onDragEnd={() => {
                setDraggedRequestId(null)
                setDropTargetId(null)
              }}
            />
          ))}
        </div>
        <QueueRequestDetails request={selectedRequest} now={now} />
      </div>
    </GlassPanel>
  )
}

function RequestCard({
  request,
  now,
  queueNumber,
  selected,
  onSelect,
  onCancel,
  onResume,
  onArchive,
  onEdit,
  onDelete,
  canMoveUp,
  canMoveDown,
  reorderDisabled,
  canDragReorder,
  onMoveUp,
  onMoveDown,
  dragging,
  dragOver,
  onDragStart,
  onDragOver,
  onDrop,
  onDragEnd,
}: {
  request: CodexRequest
  now: number
  queueNumber: number
  selected: boolean
  onSelect: () => void
  onCancel: (id: string) => Promise<void>
  onResume: (id: string) => Promise<void>
  onArchive: (id: string) => Promise<void>
  onEdit: (request: CodexRequest) => void
  onDelete: (id: string) => void
  canMoveUp: boolean
  canMoveDown: boolean
  reorderDisabled: boolean
  canDragReorder: boolean
  onMoveUp: () => void
  onMoveDown: () => void
  dragging: boolean
  dragOver: boolean
  onDragStart: (event: DragEvent<HTMLElement>) => void
  onDragOver: (event: DragEvent<HTMLElement>) => void
  onDrop: () => void
  onDragEnd: () => void
}) {
  const optimistic = isOptimisticRequest(request.id)
  const cancellable = !optimistic && (request.status === 'Queued' || request.status === 'Running' || request.status === 'CancelRequested' || request.status === 'UsageLimited')
  const resumable = !optimistic && (request.status === 'Failed' || request.status === 'Cancelled' || request.status === 'UsageLimited')
  const archivable = !optimistic && request.status === 'Succeeded' && !request.archivedAt && !request.deletedAt
  const editable = !optimistic && request.status === 'Queued'
  const deletable = !optimistic && request.status !== 'Running' && request.status !== 'CancelRequested'
  const percent = progressFor(request)
  const requestUsageDelay = request.retryAfter ? formatRemainingTime(request.retryAfter, now) : null
  const isCanceling = request.status === 'CancelRequested'
  const activeRun = activeRunFor(request)
  const duration = activeRun ? runDurationLabel(activeRun, now) : null

  return (
    <article
      className={`request-card request-card--${request.status.toLowerCase()} ${selected ? 'active' : ''} ${dragging ? 'dragging' : ''} ${dragOver ? 'drag-over' : ''}`}
      role="button"
      tabIndex={0}
      onClick={onSelect}
      onDragOver={canDragReorder ? onDragOver : undefined}
      onDrop={canDragReorder ? onDrop : undefined}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          onSelect()
        }
      }}
    >
      <div className="request-head">
        <div className="queue-position-cell">
          <div className={`queue-index ${editable ? 'queue-index--desktop-position' : ''}`} aria-label={`Queue position ${queueNumber}`}>
            {queueNumber}
          </div>
          {editable && (
            <div
              className={`queue-index queue-index--mobile-drag ${canDragReorder ? '' : 'queue-index--disabled'}`}
              aria-label={`Drag queue item ${queueNumber}`}
              draggable={canDragReorder}
              onClick={(event) => event.stopPropagation()}
              onDragStart={canDragReorder ? onDragStart : undefined}
              onDragEnd={canDragReorder ? onDragEnd : undefined}
            >
              <GripVertical size={14} />
            </div>
          )}
        </div>
        <div className="request-card-main">
          <div className="request-title-row">
            <div className="request-title-stack">
              <span className="request-title truncate" title={request.prompt}>{requestDisplayName(request)}</span>
              <ModelChips model={request.model} effort={request.modelEffort} speed={request.modelSpeed} />
            </div>
            <div className="request-card-status">
              <StatusBadge status={request.status} busy={request.status === 'Running'} />
            </div>
          </div>
          <p className="prompt-preview">{request.prompt}</p>
          <div className="request-card-meta-row">
            <span className="request-stage-chip">{stageLabel(request)}</span>
            <span>{commitModeLabel(request)}</span>
            {request.attachments.length > 0 && <span>{attachmentSummary(request.attachments)}</span>}
            {duration && <span>{duration}</span>}
            <span className="truncate">{request.machineName}</span>
            <span>created {formatDate(request.createdAt)}</span>
          </div>
        </div>
        <div className="request-actions">
          {editable && (
            <div className="request-reorder-actions" aria-label="Move queued request">
              <GlassButton
                variant="secondary"
                size="icon"
                type="button"
                disabled={!canMoveUp || reorderDisabled}
                title="Move up"
                aria-label={`Move queue item ${queueNumber} up`}
                onClick={(event) => {
                  event.stopPropagation()
                  onMoveUp()
                }}
              >
                <ArrowUp size={14} />
              </GlassButton>
              <GlassButton
                variant="secondary"
                size="icon"
                type="button"
                disabled={!canMoveDown || reorderDisabled}
                title="Move down"
                aria-label={`Move queue item ${queueNumber} down`}
                onClick={(event) => {
                  event.stopPropagation()
                  onMoveDown()
                }}
              >
                <ArrowDown size={14} />
              </GlassButton>
            </div>
          )}
          {resumable && (
            <GlassButton
              variant="secondary"
              size="sm"
              onClick={(event) => {
                event.stopPropagation()
                void onResume(request.id)
              }}
            >
              <Play size={13} /> Resume
            </GlassButton>
          )}
          {cancellable && (
            <GlassButton
              variant="danger"
              size="sm"
              disabled={isCanceling}
              onClick={(event) => {
                event.stopPropagation()
                void onCancel(request.id)
              }}
            >
              {isCanceling ? <RefreshCcw size={13} className="action-spinner" /> : <Square size={13} />}
              {isCanceling ? 'Cancelling...' : 'Cancel'}
            </GlassButton>
          )}
          {archivable && (
            <GlassButton
              variant="secondary"
              size="sm"
              onClick={(event) => {
                event.stopPropagation()
                void onArchive(request.id)
              }}
            >
              <Check size={13} /> Done
            </GlassButton>
          )}
          {editable && (
            <GlassButton
              variant="secondary"
              size="sm"
              onClick={(event) => {
                event.stopPropagation()
                onEdit(request)
              }}
            >
              <Pencil size={13} /> Edit
            </GlassButton>
          )}
          <GlassButton
            variant="ghost"
            size="icon"
            disabled={!deletable}
            title={deletable ? 'Delete queue item' : 'Cancel before deleting'}
            onClick={(event) => {
              event.stopPropagation()
              if (!deletable) return
              onDelete(request.id)
            }}
          >
            <Trash2 size={14} />
          </GlassButton>
        </div>
      </div>
      {request.status === 'UsageLimited' && (
        <UsageLimitBanner
          reason={request.retryReason}
          retryAfter={request.retryAfter}
          availableModel={request.availableModel}
          remaining={requestUsageDelay}
        />
      )}
      <ProgressLine status={request.status} percent={percent} />
    </article>
  )
}

function QueueKickButton({
  diagnostics,
  onKickQueue,
}: {
  diagnostics: QueueDiagnostics | null
  onKickQueue: () => Promise<void>
}) {
  const title = diagnostics?.lastError
    ? `Worker error: ${diagnostics.lastError}`
    : diagnostics?.isProcessing
      ? 'Worker is already processing'
      : 'Kick worker'
  const isProcessing = diagnostics?.isProcessing ?? false
  return (
    <GlassButton
      className={diagnostics?.lastError ? 'kick-worker-button kick-worker-button--bad' : 'kick-worker-button'}
      variant="secondary"
      size="sm"
      type="button"
      onClick={onKickQueue}
      disabled={isProcessing}
      title={title}
    >
      {isProcessing ? <RefreshCcw size={13} className="action-spinner" /> : <Play size={13} />}
      {isProcessing ? 'Kicking worker...' : 'Kick worker'}
    </GlassButton>
  )
}

type BodyEvent = {
  type: string
  status?: string
  text?: string
  output?: string
  exitCode?: number
  changes: Array<{ path: string, kind?: string, status?: string }>
}

type ParsedBody =
  | { kind: 'empty' }
  | { kind: 'events', events: BodyEvent[] }
  | { kind: 'json', value: unknown }
  | { kind: 'text', text: string }

function StructuredBodyView({
  content,
  emptyText = 'No content yet.',
  forceExpanded = false,
  hideJson = false,
}: {
  content?: string | null
  emptyText?: string
  forceExpanded?: boolean
  hideJson?: boolean
}) {
  const parsed = useMemo(() => parseBody(content ?? ''), [content])
  const [expanded, setExpanded] = useState(false)
  const expandable = isBodyExpandable(parsed)
  const compact = expandable && !expanded && !forceExpanded

  useEffect(() => {
    if (forceExpanded) {
      setExpanded(true)
    }
  }, [forceExpanded])

  if (parsed.kind === 'empty') {
    return <div className="body-empty">{emptyText}</div>
  }

  if (hideJson && parsed.kind === 'json' && !isDetailedCodexJson(parsed.value)) {
    const jsonText = textFromUnknownJson(parsed.value)
    if (jsonText) {
      return <BodyTextBlock text={normalizeBodyText(jsonText)} compact={false} />
    }

    return <pre className="log-block body-json">{JSON.stringify(parsed.value, null, 2)}</pre>
  }

  const body = renderStructuredBody(parsed, compact)

  if (!expandable || forceExpanded) {
    return body
  }

  return (
    <div className="body-view">
      {body}
      <GlassButton className="body-toggle" variant="ghost" size="sm" type="button" onClick={() => setExpanded((current) => !current)}>
        {expanded ? <ChevronDown size={13} /> : <ChevronRight size={13} />}
        {expanded ? 'Show less' : 'Show more'}
      </GlassButton>
    </div>
  )
}

function renderStructuredBody(parsed: Exclude<ParsedBody, { kind: 'empty' }>, compact: boolean) {
  if (parsed.kind === 'events') {
    const visibleEvents = compact ? parsed.events.slice(0, 3) : parsed.events
    const hiddenCount = parsed.events.length - visibleEvents.length
    return (
      <div className={`body-events ${compact ? 'body-events--compact' : ''}`}>
        {visibleEvents.map((event, index) => (
          <BodyEventRow key={index} event={event} compact={compact} />
        ))}
        {hiddenCount > 0 && <div className="body-hidden-count">{hiddenCount} more events</div>}
      </div>
    )
  }

  if (parsed.kind === 'json') {
    return <pre className={`log-block body-json ${compact ? 'body-compact-block' : ''}`}>{JSON.stringify(parsed.value, null, 2)}</pre>
  }

  return <BodyTextBlock text={parsed.text} compact={compact} />
}

function isDetailedCodexJson(value: unknown) {
  if (!isRecord(value)) {
    return false
  }

  const type = stringValue(value.type)
  const item = isRecord(value.item) ? value.item : undefined
  return Boolean(type || item || value.output || value.text || value.message)
}

function BodyEventRow({ event, compact }: { event: BodyEvent, compact: boolean }) {
  return (
    <article className={`body-event ${compact ? 'body-event--compact' : ''}`}>
      <div className="body-event-head">
        <span className="body-event-type">{formatEventType(event.type)}</span>
        {event.status && <span className="body-event-status">{event.status}</span>}
        {typeof event.exitCode === 'number' && <span className="body-event-status">exit {event.exitCode}</span>}
      </div>
      {event.text && <BodyEventText text={event.text} />}
      {event.output && <LogBlock text={event.output} className="body-event-output" />}
      {event.changes.length > 0 && (
        <div className="body-event-changes">
          {event.changes.map((change, index) => (
            <div key={`${change.path}:${index}`} className="body-event-change">
              <Code2 size={13} />
              <span className="truncate">{change.path}</span>
              {(change.kind || change.status) && <span className="body-event-status">{change.kind ?? change.status}</span>}
            </div>
          ))}
        </div>
      )}
    </article>
  )
}

function BodyEventText({ text }: { text: string }) {
  if (looksLikeInformativeText(text)) {
    return (
      <div className="body-event-text body-event-markdown">
        <CompletionMarkdown content={text} />
      </div>
    )
  }

  return <p className="body-event-text">{text}</p>
}

function BodyTextBlock({ text, compact }: { text: string; compact: boolean }) {
  if (looksLikeInformativeText(text)) {
    return (
      <div className={`body-text-markdown ${compact ? 'body-compact-block' : ''}`}>
        <CompletionMarkdown content={text} />
      </div>
    )
  }

  return <LogBlock text={text} className={`log-block ${compact ? 'body-compact-block' : ''}`} />
}

function LogBlock({ text, className }: { text: string; className: string }) {
  const segments = useMemo(() => parseAnsiOutput(text), [text])

  return (
    <pre className={className}>
      {segments.map((segment, index) => (
        <span key={index} className={segment.className}>{segment.text}</span>
      ))}
    </pre>
  )
}

function parseBody(content: string): ParsedBody {
  const normalizedContent = normalizeBodyText(content)
  const text = normalizedContent.trim()
  if (!text) {
    return { kind: 'empty' }
  }

  const json = tryParseJson(text)
  if (typeof json === 'string') {
    return parseBody(json)
  }

  if (isDetailedCodexJson(json)) {
    return { kind: 'events', events: [toBodyEvent(json as Record<string, unknown>)] }
  }

  if (json !== undefined) {
    return { kind: 'json', value: json }
  }

  const lines = text.split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
  const events: BodyEvent[] = []
  let parsedLines = 0
  for (const line of lines) {
    const eventJson = tryParseJson(line.startsWith('data:') ? line.slice(5).trim() : line)
    if (eventJson && typeof eventJson === 'object' && !Array.isArray(eventJson)) {
      parsedLines += 1
      events.push(toBodyEvent(eventJson as Record<string, unknown>))
    }
  }

  if (events.length > 0 && parsedLines / lines.length >= 0.4) {
    return { kind: 'events', events }
  }

  const assistantText = completionMessageFromOutput(normalizedContent)
  if (assistantText) {
    return { kind: 'text', text: assistantText }
  }

  return { kind: 'text', text: normalizedContent }
}

function normalizeBodyText(content: string) {
  let text = content.replace(/\r\n?/g, '\n').replace(/\\u001b/gi, String.fromCharCode(27))

  for (let index = 0; index < 2; index += 1) {
    const parsed = tryParseJson(text.trim())
    if (typeof parsed !== 'string' || parsed === text) {
      break
    }

    text = parsed.replace(/\r\n?/g, '\n').replace(/\\u001b/gi, String.fromCharCode(27))
  }

  if (looksLikeEscapedTransportText(text)) {
    text = text
      .replace(/\\r\\n|\\n|\\r/g, '\n')
      .replace(/\\t/g, '  ')
      .replace(/\\"/g, '"')
      .replace(/\\u001b/gi, String.fromCharCode(27))
  }

  return text
}

function looksLikeEscapedTransportText(value: string) {
  return !value.includes('\n')
    && /\\r|\\n/.test(value)
    && (/\\"(?:type|item|content|message|output)\\"/.test(value) || value.includes('\\u001b['))
}

function looksLikeInformativeText(value: string) {
  const text = value.trim()
  if (!text || text.startsWith('$ ')) {
    return false
  }

  const lines = text.split(/\n/)
  const nonEmptyLines = lines.map((line) => line.trim()).filter(Boolean)
  if (nonEmptyLines.length === 0) {
    return false
  }

  const commandLikeLines = nonEmptyLines.filter((line) =>
    line.startsWith('$ ')
    || /^[A-Z]:\\/.test(line)
    || /^[-/][\w-]+(?:\s|$)/.test(line)
    || /^[ MADRCU?!]{2}\s+\S/.test(line))

  if (commandLikeLines.length / nonEmptyLines.length > 0.35) {
    return false
  }

  return /(^|\n)\s*(#{1,3}\s+|[-*]\s+|\d+[.)]\s+|```)/.test(text)
    || /\n\s*\n/.test(text)
    || nonEmptyLines.some((line) => /[.!?]$/.test(line) && line.split(/\s+/).length >= 8)
}

function isBodyExpandable(parsed: ParsedBody) {
  if (parsed.kind === 'events') {
    return parsed.events.length > 3 || parsed.events.some((event) =>
      (event.text?.length ?? 0) > 180
      || (event.output?.length ?? 0) > 240
      || event.changes.length > 4)
  }

  if (parsed.kind === 'json') {
    return JSON.stringify(parsed.value, null, 2).length > 500
  }

  return parsed.kind === 'text' && parsed.text.length > 500
}

function tryParseJson(value: string): unknown {
  try {
    return JSON.parse(value)
  } catch {
    return undefined
  }
}

function toBodyEvent(value: Record<string, unknown>): BodyEvent {
  const item = isRecord(value.item) ? value.item : undefined
  const eventType = stringValue(value.type) ?? stringValue(item?.type) ?? 'event'
  const status = stringValue(value.status) ?? stringValue(item?.status)
  const text = textFromContent(item?.content)
    ?? textFromContent(value.content)
    ?? stringValue(value.text)
    ?? stringValue(item?.text)
    ?? stringValue(value.message)
    ?? stringValue(item?.message)
  const output = stringValue(value.output) ?? stringValue(item?.output)
  const exitCode = numberValue(value.exit_code) ?? numberValue(value.exitCode) ?? numberValue(item?.exit_code) ?? numberValue(item?.exitCode)
  const changesSource = Array.isArray(item?.changes)
    ? item?.changes
    : Array.isArray(value.changes)
      ? value.changes
      : []
  const changes = changesSource
    .filter(isRecord)
    .map((change) => ({
      path: stringValue(change.path) ?? 'unknown path',
      kind: stringValue(change.kind),
      status: stringValue(change.status),
    }))

  return { type: eventType, status, text, output, exitCode, changes }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function stringValue(value: unknown) {
  return typeof value === 'string' && value.trim() ? value : undefined
}

function numberValue(value: unknown) {
  return typeof value === 'number' ? value : undefined
}

function latestRunByPredicate(runs: readonly CodexRun[], predicate: (run: CodexRun) => boolean) {
  let best: CodexRun | undefined
  for (const run of runs) {
    if (!predicate(run)) {
      continue
    }

    if (!best) {
      best = run
      continue
    }

    if (run.createdAt > best.createdAt) {
      best = run
      continue
    }

    if (run.createdAt === best.createdAt && run.id > best.id) {
      best = run
    }
  }

  return best
}

function latestRunOfKind(runs: readonly CodexRun[], kind: RunKind) {
  return latestRunByPredicate(runs, (run) => run.kind === kind)
}

function latestRunWithCommitMetadata(runs: readonly CodexRun[]) {
  return latestRunByPredicate(runs, (run) => Boolean(run.commitSha || run.commitMessage))
}

function completionMessageForRequest(request: CodexRequest) {
  const requestRun = latestRunOfKind(request.runs, 'Request')
  const runMessage = requestRun ? completionMessageFromOutput(requestRun.output) : null
  if (runMessage) {
    return runMessage
  }

  for (const run of [...request.runs].sort((left, right) => right.createdAt.localeCompare(left.createdAt))) {
    const message = completionMessageFromOutput(run.output)
    if (message) {
      return message
    }
  }

  return cleanCompletionFallback(request.summary)
}

function completionMessageFromOutput(output: string) {
  const messages = completionMessagesFromOutput(output)
  return messages.at(-1) ?? null
}

function completionMessagesFromOutput(output: string): string[] {
  const normalizedOutput = normalizeBodyText(output)
  const parsedOutput = tryParseJson(normalizedOutput.trim())
  if (typeof parsedOutput === 'string') {
    return completionMessagesFromOutput(parsedOutput)
  }

  if (isRecord(parsedOutput)) {
    const message = assistantMessageFromEvent(parsedOutput)
    return message ? [message] : []
  }

  const events = normalizedOutput
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => tryParseJson(line.startsWith('data:') ? line.slice(5).trim() : line))
    .filter(isRecord)

  if (events.length === 0) {
    return []
  }

  const messages: string[] = []
  for (const event of events) {
    if (isTurnCompletedEvent(event)) {
      continue
    }

    const message = assistantMessageFromEvent(event)
    if (message && messages.at(-1) !== message) {
      messages.push(message)
    }
  }

  return messages
}

function assistantMessageFromEvent(event: Record<string, unknown>): string | null {
  const item = isRecord(event.item) ? event.item : event
  const type = stringValue(event.type)
  const directMessage = assistantMessageFromRecord(item, type, item === event ? undefined : event)
  if (directMessage) {
    return directMessage
  }

  return assistantMessageFromNestedOutput(event, type)
}

function assistantMessageFromRecord(value: Record<string, unknown>, eventType?: string, fallback?: Record<string, unknown>): string | null {
  const itemType = stringValue(value.type)
  const role = stringValue(value.role)
  const looksLikeCompletedMessage = isCompletedType(eventType) && isMessageType(itemType)
  const looksLikeAssistantMessage = role === 'assistant'
    || looksLikeCompletedMessage
    || isAssistantTextEventType(eventType)
    || isAssistantTextEventType(itemType)
    || isAssistantMessageType(eventType)
    || isAssistantMessageType(itemType)

  if (!looksLikeAssistantMessage) {
    return null
  }

  const message = textFromContent(value.content)
    ?? stringValue(value.message)
    ?? stringValue(value.text)
    ?? stringValue(value.output_text)
    ?? stringValue(fallback?.message)
    ?? stringValue(fallback?.text)
    ?? stringValue(fallback?.output_text)

  return sanitizeCompletionText(message)
}

function assistantMessageFromNestedOutput(event: Record<string, unknown>, eventType?: string): string | null {
  const nestedRecords: Record<string, unknown>[] = []
  const response = isRecord(event.response) ? event.response : undefined
  const data = isRecord(event.data) ? event.data : undefined
  if (response) nestedRecords.push(response)
  if (data) nestedRecords.push(data)

  for (const record of nestedRecords) {
    const directMessage = assistantMessageFromRecord(record, eventType)
    if (directMessage) {
      return directMessage
    }
  }

  const outputs = [
    ...(Array.isArray(response?.output) ? response.output : []),
    ...(Array.isArray(data?.output) ? data.output : []),
    ...(Array.isArray(event.output) ? event.output : []),
  ]

  for (let index = outputs.length - 1; index >= 0; index -= 1) {
    const output = outputs[index]
    if (!isRecord(output)) {
      continue
    }

    const message = assistantMessageFromRecord(output, eventType)
    if (message) {
      return message
    }
  }

  return null
}

function isMessageType(type?: string) {
  return Boolean(type && /(^|[._-])message$/i.test(type))
}

function isAssistantMessageType(type?: string) {
  return Boolean(type && /(^|[._-])(agent|assistant)[._-]?message$/i.test(type))
}

function isAssistantTextEventType(type?: string) {
  if (!type) {
    return false
  }

  const normalized = type.replace(/[.-]/g, '_').toLowerCase()
  return normalized === 'output_text_done'
    || normalized === 'response_output_text_done'
    || normalized.endsWith('_output_text_done')
}

function textFromContent(content: unknown): string | undefined {
  if (typeof content === 'string') {
    return content.trim() || undefined
  }

  if (!Array.isArray(content)) {
    return undefined
  }

  const parts = content
    .map((part) => {
      if (typeof part === 'string') {
        return part
      }

      if (!isRecord(part)) {
        return ''
      }

      return stringValue(part.text) ?? stringValue(part.content) ?? stringValue(part.message) ?? stringValue(part.output_text) ?? ''
    })
    .map((part) => part.trim())
    .filter(Boolean)

  return parts.length > 0 ? parts.join('\n\n') : undefined
}

function cleanCompletionFallback(summary?: string | null) {
  const value = sanitizeCompletionText(summary)
  if (!value) {
    return null
  }

  const parsed = tryParseJson(value)
  if (isRecord(parsed)) {
    if (isTelemetryCompletionEvent(parsed)) {
      return null
    }

    return assistantMessageFromEvent(parsed) ?? sanitizeCompletionText(textFromUnknownJson(parsed)) ?? value
  }

  return value
}

function sanitizeCompletionText(value?: string | null) {
  const lines = value
    ?.split(/\r?\n/)
    .filter((line) => !isCompletionNoiseLine(line))
    ?? []
  const text = lines.join('\n').trim()
  return text || null
}

function isCompletionNoiseLine(line: string) {
  const text = line.trim()
  return Boolean(text && (text.startsWith('$ ') || /^[0-9a-f]{32,64}$/i.test(text)))
}

function isCompletedType(type?: string) {
  return Boolean(type && /(^|[._-])completed$/i.test(type))
}

function isTurnCompletedType(type?: string) {
  return Boolean(type && /^turn[._-]completed$/i.test(type))
}

function isTurnCompletedEvent(event: Record<string, unknown>) {
  return isTurnCompletedType(stringValue(event.type))
}

function isTelemetryCompletionEvent(event: Record<string, unknown>) {
  if (!isTurnCompletedEvent(event)) {
    return false
  }

  return !assistantMessageFromEvent(event) && (isRecord(event.usage) || isRecord(event.token_usage) || 'usage' in event || 'token_usage' in event)
}

function formatEventType(value: string) {
  return value
    .replace(/^item\./, '')
    .replace(/_/g, ' ')
    .replace(/\b\w/g, (letter) => letter.toUpperCase())
}

function textFromUnknownJson(value: unknown): string | undefined {
  if (!isRecord(value)) {
    if (typeof value === 'string' && value.trim()) {
      return value.trim()
    }

    return undefined
  }

  const directText = stringValue(value.message) ?? stringValue(value.output) ?? stringValue(value.text)
  if (directText) {
    return directText
  }

  const nested = value.item
  if (isRecord(nested)) {
    const nestedText = stringValue(nested.message) ?? stringValue(nested.output) ?? stringValue(nested.text)
    if (nestedText) {
      return nestedText
    }
  }

  return undefined
}

function QueueRequestDetails({ request, now }: { request?: CodexRequest; now: number }) {
  const [reportOpen, setReportOpen] = useState(false)
  const separateCommitRun = request ? latestRunOfKind(request.runs, 'Commit') : undefined
  const completionMessage = request?.status === 'Succeeded' ? completionMessageForRequest(request) : null
  const showRunDetails = !completionMessage
  const reportAvailable = request?.status === 'Running' || request?.status === 'Succeeded'

  useEffect(() => {
    setReportOpen(false)
  }, [request?.id])

  if (!request) {
    return (
      <aside className="queue-detail-panel queue-detail-empty-panel" aria-label="Selected request details">
        <div className="empty-state">Select a queued request to inspect its body, status, and result.</div>
      </aside>
    )
  }

  return (
    <>
      <aside className="queue-detail-panel queue-detail-panel-compact" aria-label="Selected request details">
        {reportAvailable && (
          <div className="queue-detail-report-launch">
            <div>
              <div className="queue-detail-compact-label">Work report</div>
              <strong>{request.status === 'Succeeded' ? 'Final result ready' : 'Live work in progress'}</strong>
              <div className="meta">Read Codex's formatted results without the raw command log.</div>
            </div>
            <GlassButton variant="primary" size="sm" type="button" onClick={() => setReportOpen(true)}>
              <FileText size={13} /> Details
            </GlassButton>
          </div>
        )}
        <div className={`detail-expanded-region ${completionMessage && !showRunDetails ? 'detail-expanded-region--completion-only' : ''}`}>
          <div className="queue-detail-body">
            <div className="request-body-header">
              <div className="section-kicker">Request body</div>
              <ModelChips model={request.model} effort={request.modelEffort} speed={request.modelSpeed} />
            </div>
            <AttachmentMetadataChips attachments={request.attachments} />
            <div className="request-body-scroll">
              <StructuredBodyView content={request.prompt} />
            </div>
          </div>

          {completionMessage && (
            <div className="completion-summary">
              <div className="completion-summary-head">
                <div>
                  <div className="completion-title">Completed</div>
                  <div className="meta">{request.finishedAt ? formatDate(request.finishedAt) : 'Succeeded'}</div>
                </div>
                <StatusBadge status="Succeeded" />
              </div>
              <div className="completion-result-box">
                <CompletionMarkdown content={completionMessage} />
              </div>
            </div>
          )}

          {showRunDetails && request.runs.map((run) => (
            <div key={run.id} className="run-detail-card">
              <div className="run-detail-head">
                <div className="run-title-stack">
                  <strong>{run.kind}</strong>
                  <ModelChips model={run.model} effort={run.modelEffort} speed={run.modelSpeed} />
                </div>
                <StatusBadge status={run.status} busy={run.status === 'Running'} />
              </div>
              {run.commandPreview && <div className="command-preview">$ {run.commandPreview}</div>}
              {run.status === 'UsageLimited' && (
                <UsageLimitBanner
                  reason={run.retryReason}
                  retryAfter={run.retryAfter}
                  availableModel={run.availableModel}
                  remaining={run.retryAfter ? formatRemainingTime(run.retryAfter, now) : null}
                />
              )}
              {run.commitSha && (
                <div className="commit-box">
                  <div><strong>{run.commitSha.slice(0, 12)}</strong></div>
                  <div>{run.commitMessage}</div>
                </div>
              )}
              {run.error && <div className="error-text">{run.error}</div>}
              <DetailedCodexAnswers output={run.output} />
              <div className="run-output-head">
                <span>Run log</span>
                <span>{run.output.trim() ? `${run.output.length.toLocaleString()} chars` : 'empty'}</span>
              </div>
              <StructuredBodyView
                content={run.output}
                emptyText={runEmptyText(run, now)}
                hideJson
              />
            </div>
          ))}

          {showRunDetails && request.runs.length === 0 && (
            <div className="pending-run-row">
              <div className="run-title-stack">
                <strong>Request</strong>
                <ModelChips model={request.model} effort={request.modelEffort} speed={request.modelSpeed} />
              </div>
              <StatusBadge status="Queued" />
            </div>
          )}

          {showRunDetails && request.generateCommit && request.separateCommitSession && !separateCommitRun && (
            <div className="pending-run-row">
              <div className="run-title-stack">
                <strong>Commit</strong>
                <ModelChips model={request.commitModel || request.model} effort={request.commitModelEffort || request.modelEffort} speed={request.commitModelSpeed || request.modelSpeed} />
              </div>
              <StatusBadge status="Queued" />
            </div>
          )}
        </div>
      </aside>
      {reportOpen && (
        <WorkReportDialog request={request} now={now} onClose={() => setReportOpen(false)} />
      )}
    </>
  )
}

type WorkReportMessage = {
  kind: RunKind
  text: string
}

function reportMessagesForRequest(request: CodexRequest): WorkReportMessage[] {
  const messages: WorkReportMessage[] = []
  const seen = new Set<string>()
  const runs = [...request.runs].sort((left, right) => left.createdAt.localeCompare(right.createdAt))

  for (const run of runs) {
    for (const text of completionMessagesFromOutput(run.output)) {
      const key = `${run.kind}:${text}`
      if (!seen.has(key)) {
        seen.add(key)
        messages.push({ kind: run.kind, text })
      }
    }
  }

  return messages
}

function WorkReportDialog({ request, now, onClose }: { request: CodexRequest; now: number; onClose: () => void }) {
  const messages = useMemo(() => reportMessagesForRequest(request), [request])
  const finalMessage = request.status === 'Succeeded' ? completionMessageForRequest(request) : null
  const primaryMessage = finalMessage ?? messages.at(-1)?.text ?? null
  const supportingMessages = primaryMessage
    ? messages.filter((message) => message.text !== primaryMessage)
    : messages
  const commitRun = latestRunWithCommitMetadata(request.runs)
  const finishedAt = request.finishedAt ?? (request.status === 'Succeeded' ? request.runs.map((run) => run.finishedAt).filter(Boolean).sort().at(-1) : null)
  const elapsed = formatDurationBetween(
    request.startedAt ?? request.createdAt,
    finishedAt ?? (request.status === 'Running' ? new Date(now).toISOString() : null),
  )

  return (
    <Modal title="Codex work report" icon={<ClipboardList size={18} />} onClose={onClose} large>
      <div className="work-report" aria-live={request.status === 'Running' ? 'polite' : undefined}>
        <header className={`work-report-hero work-report-hero--${request.status.toLowerCase()}`}>
          <div className="work-report-title-stack">
            <div className="section-kicker">{request.status === 'Succeeded' ? 'Completed work' : 'Live work'}</div>
            <h3>{requestDisplayName(request)}</h3>
            <p>
              {request.status === 'Succeeded'
                ? 'Codex finished this request. The final response and meaningful work notes are collected below.'
                : 'This report refreshes while Codex works. The finished response will replace the live update when it is ready.'}
            </p>
          </div>
          <StatusBadge status={request.status} busy={request.status === 'Running'} />
        </header>

        <div className="work-report-layout">
          <main className="work-report-main">
            <section className={`work-report-section work-report-primary ${finalMessage ? 'work-report-primary--complete' : ''}`}>
              <div className="work-report-section-head">
                <div>
                  <div className="section-kicker">{finalMessage ? 'Final result' : primaryMessage ? 'Latest Codex update' : 'Result pending'}</div>
                  <h4>{finalMessage ? 'What Codex delivered' : primaryMessage ? 'What Codex has reported so far' : 'Codex is still working'}</h4>
                </div>
                {finalMessage && <Check size={20} aria-hidden="true" />}
              </div>
              {primaryMessage ? (
                <div className="work-report-content">
                  <CompletionMarkdown content={primaryMessage} />
                </div>
              ) : (
                <div className="work-report-empty">
                  No formatted Codex response has been published yet. Status and elapsed time will continue to update here.
                </div>
              )}
            </section>

            {supportingMessages.length > 0 && (
              <section className="work-report-section">
                <div className="work-report-section-head">
                  <div>
                    <div className="section-kicker">Work notes</div>
                    <h4>Additional Codex updates</h4>
                  </div>
                  <span className="work-report-count">{supportingMessages.length}</span>
                </div>
                <div className="work-report-notes">
                  {supportingMessages.map((message, index) => (
                    <article key={`${message.kind}:${index}`} className="work-report-note">
                      <span>{message.kind === 'Commit' ? 'Commit work' : 'Request work'}</span>
                      <CompletionMarkdown content={message.text} />
                    </article>
                  ))}
                </div>
              </section>
            )}
          </main>

          <aside className="work-report-sidebar" aria-label="Work report summary">
            <section className="work-report-summary-card">
              <div className="section-kicker">Summary</div>
              <dl className="work-report-facts">
                <div><dt>Project</dt><dd>{request.projectName}</dd></div>
                <div><dt>Stage</dt><dd>{stageLabel(request)}</dd></div>
                <div><dt>Elapsed</dt><dd>{elapsed ?? 'Not started'}</dd></div>
                <div><dt>{finishedAt ? 'Finished' : 'Started'}</dt><dd>{formatDate(finishedAt ?? request.startedAt ?? request.createdAt)}</dd></div>
              </dl>
              <ModelChips model={request.model} effort={request.modelEffort} speed={request.modelSpeed} />
            </section>

            <section className="work-report-summary-card">
              <div className="section-kicker">Work stages</div>
              <div className="work-report-stages">
                {request.runs.map((run) => (
                  <div key={run.id} className="work-report-stage">
                    <div>
                      <strong>{run.kind === 'Commit' ? 'Commit changes' : 'Complete request'}</strong>
                      <span>{runDurationLabel(run, now) ?? (run.startedAt ? `Started ${formatDate(run.startedAt)}` : 'Waiting to start')}</span>
                    </div>
                    <StatusBadge status={run.status} busy={run.status === 'Running'} />
                  </div>
                ))}
                {request.runs.length === 0 && <div className="work-report-empty work-report-empty--compact">Waiting for the worker to start.</div>}
              </div>
            </section>

            {(commitRun?.commitMessage || commitRun?.commitSha) && (
              <section className="work-report-summary-card work-report-commit">
                <div className="section-kicker">Commit result</div>
                {commitRun.commitMessage && <strong>{commitRun.commitMessage}</strong>}
                {commitRun.commitSha && <code title={commitRun.commitSha}>{commitRun.commitSha.slice(0, 12)}</code>}
              </section>
            )}
          </aside>
        </div>
      </div>
    </Modal>
  )
}

function DetailedCodexAnswers({ output }: { output: string }) {
  const answers = useMemo(() => completionMessagesFromOutput(output), [output])

  if (answers.length === 0) {
    return null
  }

  return (
    <section className="codex-answers" aria-label="Codex answers">
      <div className="run-output-head">
        <span>Codex answers</span>
        <span>{answers.length === 1 ? '1 answer' : `${answers.length} answers`}</span>
      </div>
      <div className="codex-answers-list">
        {answers.map((answer, index) => (
          <div key={`${index}:${answer}`} className="completion-result-box codex-answer-box">
            <CompletionMarkdown content={answer} />
          </div>
        ))}
      </div>
    </section>
  )
}

function ProjectTerminal({
  project,
  requests,
  now,
}: {
  project: Project
  requests: CodexRequest[]
  now: number
}) {
  const queuedCount = requests.filter((request) => request.status === 'Queued').length
  const runningCount = requests.filter((request) => request.status === 'Running').length
  const limitedRequest = requests.find((request) => request.status === 'UsageLimited')
  const limitedRemaining = limitedRequest?.retryAfter ? formatRemainingTime(limitedRequest.retryAfter, now) : null
  const terminalSrc = useMemo(() => apiUrl(`/projects/${project.id}/terminal/ttyd`), [project.id])

  return (
    <GlassPanel className="terminal-panel">
      <div className="terminal-status-strip" aria-label="Queue status">
        <span><strong>{queuedCount}</strong> queued</span>
        <span><strong>{runningCount}</strong> running</span>
        <span className={limitedRequest ? 'terminal-status-warn' : ''}>
          <strong>{limitedRequest ? 'Limited' : 'Ready'}</strong>
          {limitedRequest ? ` ${limitedRemaining ? `for ${limitedRemaining}` : 'retry pending'}` : ' usage'}
        </span>
        <span className="terminal-status-ok">
          <strong>ttyd</strong> shell
        </span>
      </div>
      <iframe
        key={project.id}
        className="terminal-frame"
        src={terminalSrc}
        title={`${project.machineName} terminal for ${project.name}`}
        allow="clipboard-read; clipboard-write"
      />
    </GlassPanel>
  )
}

function parseAnsiOutput(value: string) {
  const segments: Array<{ text: string; className?: string }> = []
  const escape = String.fromCharCode(27)
  let cursor = 0
  let className: string | undefined

  while (cursor < value.length) {
    const start = value.indexOf(`${escape}[`, cursor)
    if (start === -1) {
      segments.push({ text: value.slice(cursor), className })
      break
    }

    if (start > cursor) {
      segments.push({ text: value.slice(cursor, start), className })
    }

    const end = value.indexOf('m', start + 2)
    if (end === -1) {
      segments.push({ text: value.slice(start), className })
      break
    }

    const codesText = value.slice(start + 2, end)
    if (/^[\d;]*$/.test(codesText)) {
      className = ansiClassName(codesText)
      cursor = end + 1
    } else {
      segments.push({ text: value.slice(start, end + 1), className })
      cursor = end + 1
    }
  }

  return segments.length > 0 ? segments : [{ text: value }]
}

function ansiClassName(codesText: string) {
  const codes = codesText.split(';').filter(Boolean).map((code) => Number.parseInt(code, 10))
  if (codes.length === 0 || codes.includes(0)) return undefined
  if (codes.includes(31)) return 'ansi-red'
  if (codes.includes(32)) return 'ansi-green'
  if (codes.includes(33)) return 'ansi-yellow'
  if (codes.includes(34)) return 'ansi-blue'
  if (codes.includes(35)) return 'ansi-magenta'
  if (codes.includes(36)) return 'ansi-cyan'
  if (codes.includes(37)) return 'ansi-white'
  if (codes.includes(90)) return 'ansi-gray'
  if (codes.includes(91)) return 'ansi-bright-red'
  if (codes.includes(92)) return 'ansi-bright-green'
  if (codes.includes(93)) return 'ansi-bright-yellow'
  if (codes.includes(94)) return 'ansi-bright-blue'
  if (codes.includes(95)) return 'ansi-bright-magenta'
  if (codes.includes(96)) return 'ansi-bright-cyan'
  return undefined
}

function progressFor(request: CodexRequest) {
  if (request.status === 'UsageLimited') return 24
  if (request.status === 'Succeeded') return 100
  if (request.status === 'Failed' || request.status === 'Cancelled') return 100
  if (request.status === 'Queued') return 8
  const requestRun = latestRunOfKind(request.runs, 'Request')
  const commitRun = latestRunOfKind(request.runs, 'Commit')
  if (commitRun?.status === 'Running') return 82
  if (requestRun?.status === 'Succeeded' && request.generateCommit && request.separateCommitSession) return 68
  return 42
}

function activeRunFor(request: CodexRequest) {
  const requestRun = latestRunOfKind(request.runs, 'Request')
  const commitRun = latestRunOfKind(request.runs, 'Commit')
  if (request.generateCommit && request.separateCommitSession && requestRun?.status === 'Succeeded' && commitRun) {
    return commitRun
  }

  return requestRun ?? commitRun
}

function commitModeLabel(request: CodexRequest) {
  if (!request.generateCommit) return 'No commit'
  return request.separateCommitSession ? 'Separate commit' : 'Inline commit'
}

function stageLabel(request: CodexRequest) {
  const run = activeRunFor(request)
  if (!run) return request.status
  return `${run.kind} ${run.status.toLowerCase()}`
}

function runDurationLabel(run: CodexRun, now: number) {
  if (run.startedAt && run.finishedAt) {
    return formatDurationBetween(run.startedAt, run.finishedAt)
  }

  if (run.startedAt && run.status === 'Running') {
    return formatDurationBetween(run.startedAt, new Date(now).toISOString())
  }

  return null
}

function runEmptyText(run: CodexRun, now: number) {
  if (run.status === 'Queued') return 'Waiting for worker dispatch.'
  if (run.status === 'CancelRequested') return 'Cancelling this run.'
  if (run.status === 'Cancelled') return 'Cancelled before producing output.'
  if (run.status === 'Failed') return 'No output was captured for this failed run.'
  if (run.status === 'Running') {
    const duration = runDurationLabel(run, now)
    return duration ? `Running for ${duration}. Waiting for output...` : 'Running. Waiting for output...'
  }

  return 'No output captured.'
}

function UsageLimitBanner({
  reason,
  retryAfter,
  remaining,
  availableModel,
}: {
  reason?: string | null
  retryAfter?: string | null
  remaining?: string | null
  availableModel?: string | null
}) {
  const formattedRetryAfter = retryAfter ? formatDate(retryAfter) : 'Unknown time'
  return (
    <div className="run-usage-limit" role="status" aria-live="polite">
      <div className="run-usage-limit-title">Usage limit reached · request paused</div>
      <div className="run-usage-limit-reason">{reason || 'API returned a usage-limit response.'}</div>
      <div className="meta">Available again at: {formattedRetryAfter}</div>
      <div className="meta">{remaining ? `Resume in: ${remaining}` : retryAfter ? 'Retry is likely ready now.' : 'Checking retry window...'}</div>
      <div className="meta">{availableModel ? `Available model now: ${availableModel}` : 'Next queued request may proceed with the current model when quota resets.'}</div>
    </div>
  )
}

function formatRemainingTime(retryAfter: string, now: number) {
  const restartAt = Date.parse(retryAfter)
  if (Number.isNaN(restartAt)) {
    return null
  }

  const remainingMs = restartAt - now
  if (remainingMs <= 0) {
    return null
  }

  const totalSeconds = Math.ceil(remainingMs / 1000)
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  const hours = Math.floor(minutes / 60)
  const displayMinutes = minutes % 60

  if (hours > 0) {
    return `${hours}h ${displayMinutes}m`
  }

  if (minutes > 0) {
    return `${minutes}m ${seconds}s`
  }

  return `${seconds}s`
}

function RequestHistory({
  requests,
  deletedRequests,
  now,
  onDelete,
}: {
  requests: CodexRequest[]
  deletedRequests: CodexRequest[]
  now: number
  onDelete: (id: string) => void
}) {
  const [showTrash, setShowTrash] = useState(false)
  const [selectedRequestId, setSelectedRequestId] = useState<string | null>(null)
  const visibleRequests = showTrash ? deletedRequests : requests
  const selectedRequestSummary = visibleRequests.find((request) => request.id === selectedRequestId) ?? visibleRequests[0]
  const selectedRequest = useRequestDetails(selectedRequestSummary)

  useEffect(() => {
    if (visibleRequests.length === 0) {
      setSelectedRequestId(null)
      return
    }

    if (!selectedRequestId || !visibleRequests.some((request) => request.id === selectedRequestId)) {
      setSelectedRequestId(visibleRequests[0].id)
    }
  }, [selectedRequestId, visibleRequests])

  return (
    <GlassPanel className="history-panel">
      <div className="section-header">
        <h2>History</h2>
        <div className="history-actions">
          <span className="meta">{showTrash ? `${deletedRequests.length} trashed` : `${requests.length} succeeded`}</span>
          <GlassButton
            variant={showTrash ? 'secondary' : 'ghost'}
            size="icon"
            type="button"
            title={showTrash ? 'Show history' : 'Show trash'}
            onClick={() => setShowTrash((current) => !current)}
          >
            <Trash2 size={15} />
          </GlassButton>
        </div>
      </div>
      <div className="history-workbench">
        <div className="history-list" aria-label="History requests">
          {visibleRequests.slice(0, 50).map((request) => {
            const commitRun = latestRunWithCommitMetadata(request.runs)
            const completedAt = request.deletedAt ?? request.finishedAt ?? request.createdAt
            const duration = formatDurationBetween(request.startedAt ?? request.createdAt, request.finishedAt ?? request.deletedAt ?? request.createdAt)
            return (
              <article
                key={request.id}
                className={`history-row ${request.id === selectedRequest?.id ? 'active' : ''}`}
                role="button"
                tabIndex={0}
                onClick={() => setSelectedRequestId(request.id)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault()
                    setSelectedRequestId(request.id)
                  }
                }}
              >
                <div className="truncate">
                  <div className="project-name truncate" title={request.prompt}>{requestDisplayName(request)}</div>
                  <div className="history-metadata">
                    <ModelChips model={request.model} effort={request.modelEffort} speed={request.modelSpeed} />
                    <span className="model-chip model-chip--time">{request.deletedAt ? 'trashed' : 'finished'} {formatDate(completedAt)}</span>
                    {duration && <span className="model-chip model-chip--time">{duration}</span>}
                    {request.attachments.length > 0 && <span className="model-chip">{request.attachments.length} files</span>}
                    {commitRun?.commitSha && <span className="model-chip model-chip--commit">commit {commitRun.commitSha.slice(0, 12)}</span>}
                  </div>
                </div>
                <div className="history-row-actions">
                  <StatusBadge status={request.status} />
                  {!request.deletedAt && (
                    <GlassButton
                      variant="ghost"
                      size="icon"
                      type="button"
                      title="Move to trash"
                      onClick={(event) => {
                        event.stopPropagation()
                        onDelete(request.id)
                      }}
                    >
                      <Trash2 size={14} />
                    </GlassButton>
                  )}
                </div>
              </article>
            )
          })}
          {visibleRequests.length === 0 && (
            <span className="muted">{showTrash ? 'Deleted requests will appear here.' : 'Succeeded requests will appear here, newest first.'}</span>
          )}
        </div>
        <QueueRequestDetails request={selectedRequest} now={now} />
      </div>
    </GlassPanel>
  )
}

function RightRail({
  config,
  view,
  selectedProject,
  onOpenFile,
  onClose,
  onError,
}: {
  config: ApiConfig
  view: RightRailView
  selectedProject?: Project
  onOpenFile: (project: Project, path: string) => Promise<void>
  onClose: () => void
  onError: (cause: unknown) => void
}) {
  const title = view === 'git' ? 'Git' : 'Files'
  return (
    <aside className="right-rail">
      <div className="section-header">
        <h2>{title}</h2>
        <GlassButton variant="ghost" size="icon" onClick={onClose} title={`Close ${title.toLowerCase()}`}>
          <X size={16} />
        </GlassButton>
      </div>
      {selectedProject && view === 'files' ? (
        <DirectoryTree project={selectedProject} onOpenFile={onOpenFile} onError={onError} />
      ) : selectedProject && view === 'git' ? (
        <GitPanel project={selectedProject} config={config} onError={onError} />
      ) : (
        <span className="muted">Select a project to use this panel.</span>
      )}
    </aside>
  )
}

function GitPanel({
  project,
  config,
  onError,
}: {
  project: Project
  config: ApiConfig
  onError: (cause: unknown) => void
}) {
  const defaults = useMemo(() => projectModelDefaults(project, config.models), [config.models, project])
  const [status, setStatus] = useState<GitStatus | null>(null)
  const [commitMessage, setCommitMessage] = useState('')
  const [suggestionModel, setSuggestionModel] = useState<ModelValue>(defaults.commitModel)
  const [loading, setLoading] = useState(false)
  const [committing, setCommitting] = useState(false)
  const [generating, setGenerating] = useState(false)
  const [actionOutput, setActionOutput] = useState('')

  const loadStatus = useCallback(async () => {
    setLoading(true)
    try {
      const nextStatus = await api.gitStatus(project.id)
      setStatus(nextStatus)
    } catch (cause) {
      onError(cause)
    } finally {
      setLoading(false)
    }
  }, [onError, project.id])

  useEffect(() => {
    setStatus(null)
    setCommitMessage('')
    setActionOutput('')
    setSuggestionModel(defaults.commitModel)
    void loadStatus()
  }, [defaults.commitModel, loadStatus, project.id])

  const commitWithCodex = async () => {
    setGenerating(true)
    setActionOutput('')
    try {
      const result = await api.codexGitCommit(project.id, {
        model: suggestionModel.model,
        modelEffort: suggestionModel.effort,
        modelSpeed: suggestionModel.speed,
      })
      setCommitMessage('')
      setActionOutput(gitCommitSuccessMessage(result, 'Codex commit finished.'))
      await loadStatus()
    } catch (cause) {
      onError(cause)
    } finally {
      setGenerating(false)
    }
  }

  const commit = async (event: FormEvent) => {
    event.preventDefault()
    const message = commitMessage.trim()
    if (!message) return

    setCommitting(true)
    setActionOutput('')
    try {
      const result = await api.gitCommit(project.id, { message })
      setActionOutput(gitCommitSuccessMessage(result, 'Git commit finished.'))
      setCommitMessage('')
      await loadStatus()
    } catch (cause) {
      onError(cause)
    } finally {
      setCommitting(false)
    }
  }

  const changeCount = status?.changes.length ?? 0
  const clean = status?.isClean ?? false
  const gitStateLabel = clean ? 'Clean' : `${formatFileCount(changeCount)} changed`

  return (
    <div className="git-panel">
      <div className="git-panel-head">
        <div className="truncate">
          <div className="meta truncate" title={project.path}>{project.path}</div>
          <div className="git-branch-row">
            <GitBranch size={14} />
            <span className="truncate">{status?.branch ?? 'loading'}</span>
          </div>
        </div>
        <GlassButton variant="ghost" size="icon" type="button" onClick={loadStatus} disabled={loading} title="Refresh git changes">
          <RefreshCcw size={15} className={loading ? 'action-spinner' : ''} />
        </GlassButton>
      </div>

      <div className="git-panel-summary" aria-label="Git working tree summary">
        <span className={`git-state-pill ${clean ? 'git-state-pill--clean' : 'git-state-pill--dirty'}`}>
          {gitStateLabel}
        </span>
        <span>{loading ? 'Refreshing...' : status ? 'Up to date' : 'Loading status'}</span>
      </div>

      <section className="git-changes-section" aria-label="Git changes">
        <div className="run-output-head">
          <span>Git changes</span>
          <span>{loading ? 'refreshing' : formatFileCount(changeCount)}</span>
        </div>
        <div className="git-change-list">
          {status?.changes.map((change) => (
            <div key={`${change.status}:${change.path}`} className="git-change-row">
              <span className={`git-status-chip git-status-chip--${statusClassName(change.status)}`}>{formatGitStatus(change.status)}</span>
              <span className="git-file-path truncate" title={change.path}>{change.path}</span>
              <span className="git-stage-state">{formatGitStageState(change)}</span>
            </div>
          ))}
          {!loading && status && status.changes.length === 0 && <div className="empty-state">Working tree is clean.</div>}
          {loading && !status && <div className="empty-state">Loading git changes...</div>}
        </div>
        {status?.diffStat && <GitDiffStat diffStat={status.diffStat} />}
      </section>

      <form className="git-commit-form" onSubmit={commit}>
        <FieldLabel label="Commit message">
          <GlassTextarea
            value={commitMessage}
            onChange={(event) => setCommitMessage(event.target.value)}
            placeholder="Summarize the current git changes."
            rows={3}
          />
        </FieldLabel>
        <GlassButton className="manual-commit-button" variant="secondary" type="submit" disabled={!commitMessage.trim() || committing || clean}>
          {committing ? <RefreshCcw size={15} className="action-spinner" /> : <GitCommit size={15} />}
          {committing ? 'Committing...' : 'Commit'}
        </GlassButton>
      </form>

      {actionOutput && <GitActionOutput output={actionOutput} />}

      <div className="git-ai-box">
        <ModelPicker label="Codex commit" options={config.models} value={suggestionModel} onChange={setSuggestionModel} disabled={clean || generating} />
        <GlassButton variant="primary" type="button" onClick={commitWithCodex} disabled={clean || generating || !suggestionModel.model.trim()}>
          {generating ? <RefreshCcw size={15} className="action-spinner" /> : <GitCommit size={15} />}
          {generating ? 'Committing...' : 'Commit with Codex'}
        </GlassButton>
      </div>
    </div>
  )
}

function GitActionOutput({ output }: { output: string }) {
  const parsed = useMemo(() => parseGitActionOutput(output), [output])

  if (parsed.succeeded) {
    return (
      <div className="git-action-card git-action-card--success" role="status" aria-label="Git commit result">
        <div className="git-action-card-head">
          <span className="git-action-icon"><Check size={14} /></span>
          <span>{parsed.title}</span>
          {parsed.sha && <code>{parsed.sha.slice(0, 12)}</code>}
        </div>
        {parsed.message && <div className="git-action-message">{parsed.message}</div>}
        {parsed.changedFiles.length > 0 && (
          <div className="git-action-files" aria-label="Committed files">
            {parsed.changedFiles.slice(0, 4).map((file) => (
              <span key={file} className="truncate" title={file}>{file}</span>
            ))}
            {parsed.changedFiles.length > 4 && <span>{parsed.changedFiles.length - 4} more</span>}
          </div>
        )}
      </div>
    )
  }

  return (
    <div className="git-action-output" role="log" aria-label="Git commit output">
      <div className="git-action-output-title">Commit details</div>
      {parsed.lines.map((line, index) => (
        <code key={`${index}:${line}`} className={`git-action-line git-action-line--${gitActionLineKind(line)}`}>
          {line || ' '}
        </code>
      ))}
    </div>
  )
}

function gitCommitSuccessMessage(result: { success: boolean; output: string; commitSha?: string | null }, fallback: string) {
  if (!result.success) return result.output || 'Commit failed.'
  const message = gitCommitMessageFromOutput(result.output)
  return [fallback, result.commitSha, message ? `Message: ${message}` : null].filter(Boolean).join('\n')
}

function gitActionSucceeded(output: string) {
  return /\b(commit (created|finished|succeeded)|git commit finished|codex commit finished)\b/i.test(output)
}

function parseGitActionOutput(output: string) {
  const lines = output.replace(/\r/g, '').trimEnd().split('\n').filter((line) => line.trim())
  const succeeded = gitActionSucceeded(output)
  const sha = lines.find((line) => /^[0-9a-f]{7,40}$/i.test(line.trim()))?.trim()
    ?? lines.map((line) => line.match(/\b[0-9a-f]{7,40}\b/i)?.[0]).find(Boolean)
  const message = lines.map((line) => line.match(/^Message:\s*(.+)$/i)?.[1]?.trim()).find(Boolean)
  const changedFiles = lines
    .map((line) => line.match(/^[ MADRCU?!]{2}\s+(.+)$/)?.[1]?.trim())
    .filter((path): path is string => Boolean(path))
  const title = lines.find((line) => !/^[0-9a-f]{7,40}$/i.test(line.trim()) && !/^Message:/i.test(line)) ?? 'Git commit finished.'

  return { lines, succeeded, sha, message, changedFiles, title }
}

function gitActionLineKind(line: string) {
  if (line.startsWith('$ ')) return 'command'
  if (/^[0-9a-f]{7,40}$/i.test(line.trim())) return 'sha'
  if (/^\[[^\]\r\n]+\s+[0-9a-f]{7,40}\]/.test(line.trim())) return 'commit'
  if (/^(Changed files before commit|Commit created):$/.test(line.trim())) return 'heading'
  if (/^[ MADRCU?!]{2}\s+\S/.test(line)) return 'change'
  return 'text'
}

function formatFileCount(count: number) {
  return `${count} ${count === 1 ? 'file' : 'files'}`
}

function formatGitStatus(status: string) {
  return status
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .replace(/\b\w/g, (letter) => letter.toUpperCase())
}

function statusClassName(status: string) {
  return status.toLowerCase().replace(/[^a-z0-9]+/g, '-')
}

function formatGitStageState(change: GitStatus['changes'][number]) {
  if (change.staged && change.unstaged) return 'Staged + unstaged'
  if (change.staged) return 'Staged'
  if (change.unstaged) return 'Unstaged'
  return 'Tracked'
}

function gitCommitMessageFromOutput(output: string) {
  return output.replace(/\r/g, '').split('\n').map((line) => line.match(/^Message:\s*(.+)$/i)?.[1]?.trim()).find(Boolean)
}

type ParsedDiffStat = {
  entries: Array<{
    path: string
    changedLabel: string
    additions: number
    deletions: number
    isBinary: boolean
  }>
  summary: {
    files?: number
    additions?: number
    deletions?: number
    text: string
  }
}

function GitDiffStat({ diffStat }: { diffStat: string }) {
  const parsed = useMemo(() => parseDiffStat(diffStat), [diffStat])
  const fileCount = parsed.summary.files ?? parsed.entries.length
  const additions = parsed.summary.additions ?? parsed.entries.reduce((total, entry) => total + entry.additions, 0)
  const deletions = parsed.summary.deletions ?? parsed.entries.reduce((total, entry) => total + entry.deletions, 0)

  if (parsed.entries.length === 0) {
    return (
      <div className="git-stat-block">
        <div className="run-output-head">
          <span>Diff stat</span>
          <span>{parsed.summary.text || 'summary'}</span>
        </div>
        <pre className="git-action-output">{diffStat}</pre>
      </div>
    )
  }

  return (
    <div className="git-stat-block">
      <div className="run-output-head">
        <span>Diff stat</span>
        <span>{parsed.summary.text || formatFileCount(fileCount)}</span>
      </div>
      <div className="git-diff-summary" aria-label="Diff summary">
        <DiffMetric label={fileCount === 1 ? 'file' : 'files'} value={fileCount} />
        <DiffMetric label="additions" value={additions} tone="add" />
        <DiffMetric label="deletions" value={deletions} tone="delete" />
      </div>
      <div className="git-diff-stat-list">
        {parsed.entries.map((entry) => (
          <DiffStatRow key={`${entry.path}:${entry.changedLabel}`} entry={entry} />
        ))}
      </div>
    </div>
  )
}

function DiffMetric({ label, value, tone = 'neutral' }: { label: string; value: number; tone?: 'neutral' | 'add' | 'delete' }) {
  return (
    <span className={`git-diff-metric git-diff-metric--${tone}`}>
      <strong>{value.toLocaleString()}</strong>
      <span>{label}</span>
    </span>
  )
}

function DiffStatRow({ entry }: { entry: ParsedDiffStat['entries'][number] }) {
  const total = entry.additions + entry.deletions
  const additionsWidth = total > 0 ? (entry.additions / total) * 100 : 0
  const deletionsWidth = total > 0 ? (entry.deletions / total) * 100 : 0
  const changeLabel = entry.isBinary ? entry.changedLabel : `${entry.additions.toLocaleString()} + / ${entry.deletions.toLocaleString()} -`

  return (
    <div className="git-diff-stat-row">
      <span className="git-diff-stat-path truncate" title={entry.path}>{entry.path}</span>
      <span className="git-diff-stat-count">{entry.changedLabel}</span>
      <span className="git-diff-stat-change" title={changeLabel}>
        {entry.isBinary ? (
          <span className="git-diff-stat-binary">binary</span>
        ) : (
          <>
            <span className="git-diff-stat-bar git-diff-stat-bar--add" style={{ width: `${additionsWidth}%` }} />
            <span className="git-diff-stat-bar git-diff-stat-bar--delete" style={{ width: `${deletionsWidth}%` }} />
          </>
        )}
      </span>
    </div>
  )
}

function parseDiffStat(diffStat: string): ParsedDiffStat {
  const summary: ParsedDiffStat['summary'] = { text: '' }
  const entries: ParsedDiffStat['entries'] = []

  for (const rawLine of diffStat.split(/\r?\n/)) {
    const line = rawLine.trim()
    if (!line) continue

    const pipeIndex = line.lastIndexOf('|')
    if (pipeIndex === -1) {
      summary.text = line
      summary.files = numberBefore(line, /files? changed/)
      summary.additions = numberBefore(line, /insertions?\(\+\)/)
      summary.deletions = numberBefore(line, /deletions?\(-\)/)
      continue
    }

    const path = line.slice(0, pipeIndex).trim()
    const detail = line.slice(pipeIndex + 1).trim()
    const isBinary = /^bin\b/i.test(detail)
    const changedLabel = isBinary ? detail : detail.match(/^\d+/)?.[0] ?? detail
    const graph = detail.replace(/^\d+\s*/, '')
    const additions = countCharacters(graph, '+')
    const deletions = countCharacters(graph, '-')
    entries.push({ path, changedLabel, additions, deletions, isBinary })
  }

  return { entries, summary }
}

function numberBefore(value: string, marker: RegExp) {
  const match = value.match(new RegExp(`(\\d+)\\s+${marker.source}`, marker.flags))
  return match ? Number.parseInt(match[1], 10) : undefined
}

function countCharacters(value: string, character: string) {
  let count = 0
  for (const current of value) {
    if (current === character) count += 1
  }
  return count
}

type MarkdownBlock =
  | { kind: 'heading', level: 1 | 2 | 3, text: string }
  | { kind: 'paragraph', text: string }
  | { kind: 'list', ordered: boolean, items: string[] }
  | { kind: 'code', code: string }

function CompletionMarkdown({ content }: { content: string }) {
  const blocks = useMemo(() => parseCompletionMarkdown(content), [content])

  return (
    <div className="completion-markdown">
      {blocks.map((block, index) => renderMarkdownBlock(block, index))}
    </div>
  )
}

function renderMarkdownBlock(block: MarkdownBlock, index: number) {
  switch (block.kind) {
    case 'heading': {
      const HeadingTag = `h${block.level}` as 'h1' | 'h2' | 'h3'
      return <HeadingTag key={index}>{renderMarkdownInline(block.text)}</HeadingTag>
    }
    case 'list': {
      const ListTag = block.ordered ? 'ol' : 'ul'
      return (
        <ListTag key={index}>
          {block.items.map((item, itemIndex) => (
            <li key={itemIndex}>{renderMarkdownInline(item)}</li>
          ))}
        </ListTag>
      )
    }
    case 'code':
      return (
        <pre key={index} className="completion-code-block">
          <code>{block.code}</code>
        </pre>
      )
    case 'paragraph':
      return <p key={index}>{renderMarkdownInline(block.text)}</p>
  }
}

function parseCompletionMarkdown(content: string): MarkdownBlock[] {
  const lines = content.replace(/\r\n?/g, '\n').split('\n')
  const blocks: MarkdownBlock[] = []
  let index = 0

  while (index < lines.length) {
    const line = lines[index]
    const trimmed = line.trim()

    if (!trimmed) {
      index += 1
      continue
    }

    if (trimmed.startsWith('```')) {
      const codeLines: string[] = []
      index += 1
      while (index < lines.length && !lines[index].trim().startsWith('```')) {
        codeLines.push(lines[index])
        index += 1
      }
      if (index < lines.length) index += 1
      blocks.push({ kind: 'code', code: codeLines.join('\n') })
      continue
    }

    const heading = trimmed.match(/^(#{1,3})\s+(.+)$/)
    if (heading) {
      blocks.push({ kind: 'heading', level: heading[1].length as 1 | 2 | 3, text: heading[2].trim() })
      index += 1
      continue
    }

    const unordered = trimmed.match(/^[-*]\s+(.+)$/)
    const ordered = trimmed.match(/^\d+[.)]\s+(.+)$/)
    if (unordered || ordered) {
      const orderedList = Boolean(ordered)
      const items: string[] = []
      while (index < lines.length) {
        const itemLine = lines[index].trim()
        const item = orderedList
          ? itemLine.match(/^\d+[.)]\s+(.+)$/)
          : itemLine.match(/^[-*]\s+(.+)$/)
        if (!item) break
        items.push(item[1].trim())
        index += 1
      }
      blocks.push({ kind: 'list', ordered: orderedList, items })
      continue
    }

    const paragraphLines = [trimmed]
    index += 1
    while (index < lines.length && lines[index].trim() && !startsMarkdownBlock(lines[index].trim())) {
      paragraphLines.push(lines[index].trim())
      index += 1
    }
    blocks.push({ kind: 'paragraph', text: paragraphLines.join('\n') })
  }

  return blocks
}

function startsMarkdownBlock(trimmed: string) {
  return trimmed.startsWith('```')
    || /^(#{1,3})\s+/.test(trimmed)
    || /^[-*]\s+/.test(trimmed)
    || /^\d+[.)]\s+/.test(trimmed)
}

function renderMarkdownInline(text: string): ReactNode[] {
  const nodes: ReactNode[] = []
  const pattern = /(\*\*[^*\n]+\*\*|`[^`\n]+`|\[[^\]\n]+\]\(https?:\/\/[^)\s]+\))/g
  let cursor = 0

  for (const match of text.matchAll(pattern)) {
    const index = match.index ?? 0
    if (index > cursor) {
      nodes.push(text.slice(cursor, index))
    }

    const value = match[0]
    if (value.startsWith('**')) {
      nodes.push(<strong key={nodes.length}>{value.slice(2, -2)}</strong>)
    } else if (value.startsWith('`')) {
      nodes.push(<code key={nodes.length}>{value.slice(1, -1)}</code>)
    } else {
      const link = value.match(/^\[([^\]\n]+)\]\((https?:\/\/[^)\s]+)\)$/)
      if (link) {
        nodes.push(
          <a key={nodes.length} href={link[2]} target="_blank" rel="noreferrer">
            {link[1]}
          </a>
        )
      } else {
        nodes.push(value)
      }
    }
    cursor = index + value.length
  }

  if (cursor < text.length) {
    nodes.push(text.slice(cursor))
  }

  return nodes
}

function DirectoryTree({
  project,
  onOpenFile,
  onError,
}: {
  project: Project
  onOpenFile: (project: Project, path: string) => Promise<void>
  onError: (cause: unknown) => void
}) {
  const [entriesByPath, setEntriesByPath] = useState<Record<string, FileTreeEntry[]>>({})
  const [expanded, setExpanded] = useState<Record<string, boolean>>({ '': true })

  const load = useCallback(async (path = '') => {
    try {
      const entries = await api.tree(project.id, path)
      setEntriesByPath((current) => ({ ...current, [path]: entries }))
    } catch (cause) {
      onError(cause)
    }
  }, [onError, project.id])

  useEffect(() => {
    setEntriesByPath({})
    setExpanded({ '': true })
    load('')
  }, [load, project.id])

  const toggle = async (entry: FileTreeEntry) => {
    if (!entry.isDirectory) {
      await onOpenFile(project, entry.path)
      return
    }
    setExpanded((current) => ({ ...current, [entry.path]: !current[entry.path] }))
    if (!entriesByPath[entry.path]) {
      await load(entry.path)
    }
  }

  const renderEntries = (path = '') => (
    <div className={path ? 'tree-children' : 'tree-list'}>
      {(entriesByPath[path] ?? []).map((entry) => (
        <div key={entry.path}>
          <button type="button" className="tree-row" onClick={() => toggle(entry)}>
            {entry.isDirectory ? (expanded[entry.path] ? <ChevronDown size={14} /> : <ChevronRight size={14} />) : <Code2 size={14} />}
            {entry.isDirectory ? (expanded[entry.path] ? <FolderOpen size={15} /> : <Folder size={15} />) : null}
            <span className="truncate">{entry.name}</span>
          </button>
          {entry.isDirectory && expanded[entry.path] && renderEntries(entry.path)}
        </div>
      ))}
    </div>
  )

  return (
    <div className="section-stack directory-tree">
      <div className="meta truncate">{project.path}</div>
      {renderEntries()}
    </div>
  )
}

type CodeLanguage = {
  id: 'assembly' | 'c' | 'cpp' | 'csharp' | 'css' | 'html' | 'json' | 'python' | 'tsx' | 'xaml' | 'xml' | 'text'
  label: string
  family: 'clike' | 'css' | 'json' | 'markup' | 'python' | 'assembly' | 'text'
}

const languageByExtension: Record<string, CodeLanguage> = {
  '.asm': { id: 'assembly', label: 'Assembly', family: 'assembly' },
  '.c': { id: 'c', label: 'C', family: 'clike' },
  '.cc': { id: 'cpp', label: 'C++', family: 'clike' },
  '.cpp': { id: 'cpp', label: 'C++', family: 'clike' },
  '.cs': { id: 'csharp', label: 'C#', family: 'clike' },
  '.css': { id: 'css', label: 'CSS', family: 'css' },
  '.cxx': { id: 'cpp', label: 'C++', family: 'clike' },
  '.h': { id: 'c', label: 'C/C++ Header', family: 'clike' },
  '.hpp': { id: 'cpp', label: 'C++ Header', family: 'clike' },
  '.html': { id: 'html', label: 'HTML', family: 'markup' },
  '.htm': { id: 'html', label: 'HTML', family: 'markup' },
  '.json': { id: 'json', label: 'JSON', family: 'json' },
  '.jsonc': { id: 'json', label: 'JSONC', family: 'json' },
  '.jsx': { id: 'tsx', label: 'JSX', family: 'clike' },
  '.mjs': { id: 'tsx', label: 'JavaScript', family: 'clike' },
  '.py': { id: 'python', label: 'Python', family: 'python' },
  '.s': { id: 'assembly', label: 'Assembly', family: 'assembly' },
  '.scss': { id: 'css', label: 'SCSS', family: 'css' },
  '.ts': { id: 'tsx', label: 'TypeScript', family: 'clike' },
  '.tsx': { id: 'tsx', label: 'TSX', family: 'clike' },
  '.xaml': { id: 'xaml', label: 'XAML', family: 'markup' },
  '.xml': { id: 'xml', label: 'XML', family: 'markup' },
}

const plainLanguage: CodeLanguage = { id: 'text', label: 'Plain text', family: 'text' }

const cLikeKeywords = new Set([
  'abstract', 'as', 'async', 'await', 'base', 'bool', 'break', 'case', 'catch', 'char', 'class', 'const', 'continue',
  'default', 'delegate', 'delete', 'do', 'double', 'else', 'enum', 'event', 'export', 'extern', 'false', 'finally', 'fixed',
  'float', 'for', 'foreach', 'from', 'function', 'get', 'if', 'implements', 'import', 'in', 'inline', 'int', 'interface',
  'internal', 'is', 'let', 'long', 'namespace', 'new', 'null', 'operator', 'out', 'override', 'private', 'protected',
  'public', 'readonly', 'record', 'ref', 'return', 'sealed', 'set', 'short', 'signed', 'sizeof', 'static', 'string',
  'struct', 'switch', 'template', 'this', 'throw', 'true', 'try', 'type', 'typedef', 'typeof', 'uint', 'ulong', 'unsigned',
  'using', 'var', 'virtual', 'void', 'volatile', 'while'
])

const pythonKeywords = new Set([
  'and', 'as', 'assert', 'async', 'await', 'break', 'class', 'continue', 'def', 'del', 'elif', 'else', 'except', 'False',
  'finally', 'for', 'from', 'global', 'if', 'import', 'in', 'is', 'lambda', 'None', 'nonlocal', 'not', 'or', 'pass',
  'raise', 'return', 'True', 'try', 'while', 'with', 'yield'
])

function detectCodeLanguage(path: string): CodeLanguage {
  const fileName = path.split(/[\\/]/).at(-1) ?? path
  if (fileName.endsWith('.S')) {
    return languageByExtension['.s']
  }

  const dot = fileName.lastIndexOf('.')
  if (dot < 0) {
    return plainLanguage
  }

  return languageByExtension[fileName.slice(dot).toLowerCase()] ?? plainLanguage
}

function highlightCode(content: string, language: CodeLanguage): ReactNode {
  if (language.family === 'text') {
    return content
  }

  const tokens = tokenizeCode(content, language)
  return tokens.map((token, index) => token.kind === 'text'
    ? token.value
    : <span key={index} className={`syntax-${token.kind}`}>{token.value}</span>)
}

function tokenizeCode(content: string, language: CodeLanguage) {
  const pattern = tokenPattern(language.family)
  const tokens: Array<{ kind: string, value: string }> = []
  let cursor = 0

  for (const match of content.matchAll(pattern)) {
    const index = match.index ?? 0
    if (index > cursor) {
      tokens.push({ kind: 'text', value: content.slice(cursor, index) })
    }

    const value = match[0]
    tokens.push({ kind: tokenKind(value, language), value })
    cursor = index + value.length
  }

  if (cursor < content.length) {
    tokens.push({ kind: 'text', value: content.slice(cursor) })
  }

  return tokens
}

function tokenPattern(family: CodeLanguage['family']) {
  switch (family) {
    case 'assembly':
      return /(;.*|#.*|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|\b(?:0x[\da-fA-F]+|\d+(?:\.\d+)?)\b|^\s*[A-Za-z_.$][\w.$]*:|\b[A-Za-z_.$][\w.$]*\b)/gm
    case 'css':
      return /(\/\*[\s\S]*?\*\/|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|#[\da-fA-F]{3,8}\b|\b(?:-?\d+(?:\.\d+)?(?:px|rem|em|vh|vw|%|s|ms)?)\b|[.#]?-{0,2}[A-Za-z_][\w-]*(?=\s*:)|[.#]?[A-Za-z_][\w-]*(?=[\s,{]))/g
    case 'json':
      return /(\/\/.*|\/\*[\s\S]*?\*\/|"(?:\\.|[^"\\])*"(?=\s*:)|"(?:\\.|[^"\\])*"|\b(?:true|false|null)\b|-?\b\d+(?:\.\d+)?(?:e[+-]?\d+)?\b)/gi
    case 'markup':
      return /(<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<\/?[A-Za-z][\w:.-]*|\/?>|[A-Za-z_:][\w:.-]*(?=\s*=)|"(?:&quot;|[^"])*"|'(?:&apos;|[^'])*'|&[A-Za-z#0-9]+;)/g
    case 'python':
      return /(#.*|"""[\s\S]*?"""|'''[\s\S]*?'''|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|\b(?:0x[\da-fA-F]+|\d+(?:\.\d+)?)\b|\b[A-Za-z_]\w*\b)/g
    default:
      return /(\/\/.*|\/\*[\s\S]*?\*\/|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|`(?:\\.|[^`\\])*`|\b(?:0x[\da-fA-F]+|\d+(?:\.\d+)?)\b|\b[A-Za-z_]\w*\b)/g
  }
}

function tokenKind(value: string, language: CodeLanguage) {
  if (value.startsWith('//')
    || value.startsWith('/*')
    || (value.startsWith('#') && language.family !== 'css')
    || value.startsWith(';')
    || value.startsWith('<!--')
    || value.startsWith('<![CDATA[')) {
    return 'comment'
  }

  if (value.startsWith('"') || value.startsWith("'") || value.startsWith('`')) {
    return language.family === 'json' && value.endsWith('"') && /"\s*$/.test(value) ? 'string' : 'string'
  }

  if (/^<\/?[A-Za-z]/.test(value) || value === '/>' || value === '>') {
    return 'tag'
  }

  if (/^&[A-Za-z#0-9]+;$/.test(value)) {
    return 'entity'
  }

  if (/^(?:#[\da-fA-F]{3,8}|-?\d|0x)/.test(value)) {
    return 'number'
  }

  if (value.endsWith(':') || /^[.#]?-{0,2}[A-Za-z_][\w-]*(?=$)/.test(value) && language.family === 'css') {
    return 'property'
  }

  if (language.family === 'json' && (value === 'true' || value === 'false' || value === 'null')) {
    return 'keyword'
  }

  if (language.family === 'python' && pythonKeywords.has(value)) {
    return 'keyword'
  }

  if (language.family === 'clike' && cLikeKeywords.has(value)) {
    return 'keyword'
  }

  if (language.family === 'markup') {
    return 'property'
  }

  return 'text'
}

function CodeViewer({ file }: { file: OpenFile }) {
  const language = useMemo(() => detectCodeLanguage(file.path), [file.path])
  const highlighted = useMemo(() => highlightCode(file.content, language), [file.content, language])

  return (
    <GlassPanel className="code-viewer">
      <div className="section-header">
        <h2 className="truncate">{file.path}</h2>
        <span className="meta">{language.label} · {file.size.toLocaleString()} bytes{file.truncated ? ' · truncated' : ''}</span>
      </div>
      <pre>
        <code className={`language-${language.id}`}>{highlighted}</code>
      </pre>
    </GlassPanel>
  )
}

function formatModel(model: string, effort?: string | null, speed?: string | null) {
  const labels: Record<string, string> = {
    low: 'light',
    medium: 'medium',
    high: 'high',
    xhigh: 'extra high',
    ultra: 'ultra',
  }
  const parts = [model]
  if (effort) parts.push(labels[effort] ?? effort)
  if (speed === 'priority') parts.push('x1.5')
  return parts.join(' ')
}

function formatDurationBetween(start?: string | null, end?: string | null) {
  if (!start || !end) return null
  const startTime = Date.parse(start)
  const endTime = Date.parse(end)
  if (Number.isNaN(startTime) || Number.isNaN(endTime) || endTime < startTime) return null

  const totalSeconds = Math.max(1, Math.round((endTime - startTime) / 1000))
  const hours = Math.floor(totalSeconds / 3600)
  const minutes = Math.floor((totalSeconds % 3600) / 60)
  const seconds = totalSeconds % 60

  if (hours > 0) return `${hours}h ${minutes}m`
  if (minutes > 0) return `${minutes}m ${seconds}s`
  return `${seconds}s`
}

function requestDisplayName(request: CodexRequest) {
  const normalized = request.prompt.replace(/\s+/g, ' ').trim()
  if (!normalized) {
    return `Request ${shortId(request.id)}`
  }

  return normalized.length <= 80 ? normalized : normalized.slice(0, 80).trimEnd()
}

function attachmentSummary(attachments: Array<{ contentType: string }>) {
  const imageCount = attachments.filter((attachment) => attachment.contentType.startsWith('image/')).length
  const fileLabel = `${attachments.length} ${attachments.length === 1 ? 'file' : 'files'}`
  return imageCount > 0 ? `${fileLabel}, ${imageCount} image${imageCount === 1 ? '' : 's'}` : fileLabel
}

function ModelChips({ model, effort, speed }: { model: string, effort?: string | null, speed?: string | null }) {
  const effortLabels: Record<string, string> = {
    low: 'light',
    medium: 'medium',
    high: 'high',
    xhigh: 'xhigh',
    ultra: 'ultra',
  }
  const normalizedSpeed = speed === 'priority' ? 'x1.5' : speed || 'normal'

  return (
    <div className="model-chip-row" aria-label="Selected model settings">
      <span className="model-chip model-chip--model">{model}</span>
      {effort && <span className={`model-chip model-chip--effort model-chip--effort-${effort}`}>{effortLabels[effort] ?? effort}</span>}
      <span className={`model-chip model-chip--speed ${speed === 'priority' ? 'model-chip--speed-priority' : ''}`}>{normalizedSpeed}</span>
    </div>
  )
}

export default App
