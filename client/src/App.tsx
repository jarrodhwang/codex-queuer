import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ClipboardEvent, DragEvent, FormEvent, KeyboardEvent, ReactNode } from 'react'
import { createPortal } from 'react-dom'
import {
  Check,
  ChevronDown,
  ChevronRight,
  ClipboardList,
  Code2,
  Folder,
  FolderOpen,
  FolderPlus,
  GripVertical,
  History,
  Menu,
  Monitor,
  Pencil,
  Play,
  Plus,
  RefreshCcw,
  Server,
  Settings,
  Square,
  Terminal as TerminalIcon,
  Trash2,
  X,
} from 'lucide-react'
import { ApiError, api, apiWebSocketUrl, getStoredToken, storeToken } from '@/api/client'
import type {
  ApiConfig,
  CodexRequest,
  FileContent,
  FileTreeEntry,
  Machine,
  MachineKind,
  MachinePlatform,
  ModelOption,
  Project,
  QueueAttachment,
  QueueDiagnostics,
  SaveMachineRequest,
  SaveProjectRequest,
  UpdateQueueRequest,
} from '@/api/types'
import { FieldLabel, GlassButton, GlassInput, GlassPanel, GlassSelect, GlassTextarea } from '@/components/einui/Glass'
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

const defaultModels: ModelOption[] = [
  { label: 'GPT-5.5', model: 'gpt-5.5', supportsPriority: true },
  { label: 'GPT-5.4', model: 'gpt-5.4', supportsPriority: true },
  { label: 'GPT-5.4 Mini', model: 'gpt-5.4-mini', supportsPriority: false },
  { label: 'GPT-5.3 Codex Spark', model: 'gpt-5.3-codex-spark', supportsPriority: false },
]

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

async function readQueueAttachment(file: File): Promise<QueueAttachment> {
  if (file.size > 5_000_000) {
    throw new Error(`${file.name} is larger than 5 MB.`)
  }

  const buffer = await file.arrayBuffer()
  return {
    name: file.name,
    contentType: file.type || 'application/octet-stream',
    size: file.size,
    contentBase64: arrayBufferToBase64(buffer),
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

function App() {
  const [config, setConfig] = useState<ApiConfig>({ requiresToken: false, models: defaultModels })
  const [machines, setMachines] = useState<Machine[]>([])
  const [projects, setProjects] = useState<Project[]>([])
  const [requests, setRequests] = useState<CodexRequest[]>([])
  const [queueDiagnostics, setQueueDiagnostics] = useState<QueueDiagnostics | null>(null)
  const [selectedProjectId, setSelectedProjectId] = useState('')
  const [rightOpen, setRightOpen] = useState(true)
  const [authBlocked, setAuthBlocked] = useState(false)
  const [error, setError] = useState('')
  const [openFiles, setOpenFiles] = useState<OpenFile[]>([])
  const [activeFileKey, setActiveFileKey] = useState<string | null>(null)
  const [liveNow, setLiveNow] = useState(() => Date.now())

  const selectedProject = projects.find((project) => project.id === selectedProjectId) ?? projects[0]
  const activeFile = openFiles.find((file) => file.key === activeFileKey)

  const loadStatic = useCallback(async () => {
    const nextConfig = await api.config()
    setConfig(nextConfig)
    if (nextConfig.requiresToken && !getStoredToken()) {
      setAuthBlocked(true)
      return
    }
    const [nextMachines, nextProjects] = await Promise.all([api.machines(), api.projects()])
    setMachines(nextMachines)
    setProjects(nextProjects)
    setSelectedProjectId((current) => current || nextProjects[0]?.id || '')
  }, [])

  const loadLive = useCallback(async () => {
    const nextRequests = await api.requests(undefined, true)
    setRequests(nextRequests)
    try {
      setQueueDiagnostics(await api.queueDiagnostics())
    } catch {
      setQueueDiagnostics(null)
    }
  }, [])

  const handleApiError = useCallback((cause: unknown) => {
    if (cause instanceof ApiError && cause.status === 401) {
      setAuthBlocked(true)
      return
    }
    setError(cause instanceof Error ? cause.message : 'Request failed.')
  }, [])

  useEffect(() => {
    loadStatic().then(() => loadLive()).catch(handleApiError)
  }, [handleApiError, loadLive, loadStatic])

  useEffect(() => {
    if (authBlocked) return
    const timer = window.setInterval(() => {
      loadLive().catch(handleApiError)
    }, 2200)
    return () => window.clearInterval(timer)
  }, [authBlocked, handleApiError, loadLive])

  useEffect(() => {
    const timer = window.setInterval(() => {
      setLiveNow(Date.now())
    }, 1000)
    return () => window.clearInterval(timer)
  }, [])

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

  const removeProject = async (project: Project) => {
    const confirmed = window.confirm(`Remove "${project.name}" from Codex Queue? This only removes the project from this web app. It will not delete files or folders from disk.`)
    if (!confirmed) return

    setError('')
    try {
      await api.deleteProject(project.id)
      const remainingProjects = projects.filter((item) => item.id !== project.id)
      const remainingOpenFiles = openFiles.filter((file) => file.projectId !== project.id)
      setProjects(remainingProjects)
      setSelectedProjectId(remainingProjects[0]?.id ?? '')
      setOpenFiles(remainingOpenFiles)
      setActiveFileKey((current) => (current?.startsWith(`${project.id}:`) ? remainingOpenFiles.at(-1)?.key ?? null : current))
      await loadLive()
    } catch (cause) {
      handleApiError(cause)
    }
  }

  const kickQueue = async () => {
    setError('')
    try {
      await api.kickQueue()
      await loadLive()
    } catch (cause) {
      handleApiError(cause)
    }
  }

  const archiveRequest = async (id: string) => {
    setError('')
    try {
      await api.archiveRequest(id)
      await loadLive()
    } catch (cause) {
      handleApiError(cause)
    }
  }

  const archiveRequests = async (ids: string[]) => {
    if (ids.length === 0) return

    setError('')
    try {
      await Promise.all(ids.map((id) => api.archiveRequest(id)))
      await loadLive()
    } catch (cause) {
      handleApiError(cause)
    }
  }

  const updateRequest = async (id: string, request: UpdateQueueRequest) => {
    setError('')
    try {
      await api.updateRequest(id, request)
      await loadLive()
    } catch (cause) {
      handleApiError(cause)
      throw cause
    }
  }

  const reorderRequests = async (projectId: string, requestIds: string[]) => {
    setError('')
    try {
      await api.reorderRequests(projectId, requestIds)
      await loadLive()
    } catch (cause) {
      handleApiError(cause)
      throw cause
    }
  }

  const deleteRequest = async (id: string) => {
    const request = requests.find((item) => item.id === id)
    const label = request ? shortId(request.id) : 'this request'
    const confirmed = window.confirm(`Move ${label} to trash? This will hide it from Queue and History unless the History trash view is enabled.`)
    if (!confirmed) return

    setError('')
    try {
      await api.deleteRequest(id)
      await loadLive()
    } catch (cause) {
      handleApiError(cause)
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
              requests={requests}
              diagnostics={queueDiagnostics}
              now={liveNow}
              onCreated={loadLive}
            onCancel={async (id) => {
              await api.cancelRequest(id)
              await loadLive()
            }}
            onResume={async (id) => {
              await api.resumeRequest(id)
              await loadLive()
            }}
            onArchiveRequest={archiveRequest}
            onArchiveRequests={archiveRequests}
            onUpdateRequest={updateRequest}
            onReorderRequests={reorderRequests}
            onDeleteRequest={deleteRequest}
            onUpdateProjectDefaults={updateProjectDefaults}
            onKickQueue={kickQueue}
            onError={handleApiError}
            error={error}
            onRefresh={refreshAll}
            onToggleFiles={() => setRightOpen((open) => !open)}
          />
        )}
      </main>

      {rightOpen && (
        <RightRail
          selectedProject={selectedProject}
          onOpenFile={openFile}
          onClose={() => setRightOpen(false)}
          onError={handleApiError}
        />
      )}
    </div>
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
  machines: Machine[]
  projects: Project[]
  requests: CodexRequest[]
  now: number
  selectedProjectId: string
  onSelectProject: (id: string) => void
  onRenameProject: (project: Project, name: string) => Promise<void>
  onRemoveProject: (project: Project) => Promise<void>
  onChanged: () => Promise<void>
  onError: (cause: unknown) => void
}) {
  const [projectModalOpen, setProjectModalOpen] = useState(false)
  const [machineModalOpen, setMachineModalOpen] = useState(false)
  const [projectDetailsId, setProjectDetailsId] = useState<string | null>(null)
  const [machineStatuses, setMachineStatuses] = useState<Record<string, { checking: boolean; success?: boolean; output: string }>>({})
  const detailProject = projects.find((project) => project.id === projectDetailsId)
  const usageLimitedRequests = useMemo(() => requests
    .filter((request) => !request.deletedAt && request.status === 'UsageLimited')
    .toSorted((left, right) => Date.parse(left.retryAfter ?? left.createdAt) - Date.parse(right.retryAfter ?? right.createdAt)),
  [requests])
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
      <GlassPanel>
        <div className="section-header">
          <h2>Projects</h2>
          <div className="sidebar-actions">
            <GlassButton variant="ghost" size="icon" onClick={() => setProjectModalOpen(true)} title="Add project">
              <FolderPlus size={16} />
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
                machineProjects.map((project) => (
                  <div
                    key={project.id}
                    className={`project-item ${project.id === selectedProjectId ? 'active' : ''}`}
                  >
                    <button type="button" className="project-item-main" onClick={() => onSelectProject(project.id)}>
                      <div className="project-name truncate">{project.name}</div>
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
                ))
              )}
            </div>
          ))}
          {machines.length === 0 && <div className="empty-state">No machines configured</div>}
        </div>
      </GlassPanel>

      <UsageLimitSidebarPanel requests={usageLimitedRequests} now={now} />

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
            await onRemoveProject(project)
            setProjectDetailsId(null)
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

function UsageLimitSidebarPanel({ requests, now }: { requests: CodexRequest[], now: number }) {
  const active = requests.length > 0

  return (
    <div className={`usage-sidebar ${active ? 'usage-sidebar--limited' : ''}`}>
      <div className="usage-sidebar-head">
        <span>GPT usage</span>
        <span className="usage-sidebar-pill">{active ? `${requests.length} paused` : 'OK'}</span>
      </div>
      {active ? (
        <div className="usage-sidebar-list">
          {requests.slice(0, 3).map((request) => {
            const limitedRun = request.runs.find((run) => run.status === 'UsageLimited')
            const model = limitedRun?.model || request.model
            const retryAfter = limitedRun?.retryAfter ?? request.retryAfter
            const remaining = retryAfter ? formatRemainingTime(retryAfter, now) : null
            return (
              <div key={request.id} className="usage-sidebar-item">
                <div className="truncate">{model}</div>
                <div className="meta truncate">{request.projectName || shortId(request.id)}</div>
                <div className="meta truncate">{remaining ? `Resume in ${remaining}` : retryAfter ? `Ready ${formatDate(retryAfter)}` : 'Retry window unknown'}</div>
              </div>
            )
          })}
          {requests.length > 3 && <div className="meta">+{requests.length - 3} more paused</div>}
        </div>
      ) : (
        <div className="meta">No model is paused by usage limits.</div>
      )}
    </div>
  )
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
                <GlassButton variant="danger" size="sm" type="button" onClick={() => remove(editingId)}>
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

function Modal({ title, icon, children, onClose, wide = false }: { title: string; icon: React.ReactNode; children: React.ReactNode; onClose: () => void; wide?: boolean }) {
  return createPortal(
    <div className="modal-backdrop" role="dialog" aria-modal="true" aria-label={title}>
      <div className={`modal ${wide ? 'modal--wide' : ''}`}>
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
  requests,
  diagnostics,
  now,
  onCreated,
  onCancel,
  onResume,
  onArchiveRequest,
  onArchiveRequests,
  onUpdateRequest,
  onReorderRequests,
  onDeleteRequest,
  onUpdateProjectDefaults,
  onKickQueue,
  onError,
  error,
  onRefresh,
  onToggleFiles,
}: {
  config: ApiConfig
  selectedProject?: Project
  requests: CodexRequest[]
  diagnostics: QueueDiagnostics | null
  now: number
  onCreated: () => Promise<void>
  onCancel: (id: string) => Promise<void>
  onResume: (id: string) => Promise<void>
  onArchiveRequest: (id: string) => Promise<void>
  onArchiveRequests: (ids: string[]) => Promise<void>
  onUpdateRequest: (id: string, request: UpdateQueueRequest) => Promise<void>
  onReorderRequests: (projectId: string, requestIds: string[]) => Promise<void>
  onDeleteRequest: (id: string) => Promise<void>
  onUpdateProjectDefaults: (project: Project, defaults: ProjectModelDefaults) => Promise<void>
  onKickQueue: () => Promise<void>
  onError: (cause: unknown) => void
  error: string
  onRefresh: () => Promise<void>
  onToggleFiles: () => void
}) {
  const [activeTab, setActiveTab] = useState<'queue' | 'history' | 'terminal'>('queue')
  const [editingRequest, setEditingRequest] = useState<CodexRequest | null>(null)
  const scopedRequests = useMemo(() => {
    if (!selectedProject) {
      return []
    }

    return requests
      .filter((request) => request.projectId === selectedProject.id)
      .toSorted((left, right) => Date.parse(left.createdAt) - Date.parse(right.createdAt))
  }, [requests, selectedProject])

  const historyRequests = useMemo(() => scopedRequests
    .filter((request) => !request.deletedAt && request.status === 'Succeeded')
    .toSorted((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt)),
  [scopedRequests])
  const queueRequests = useMemo(() => scopedRequests
    .filter((request) => !request.deletedAt && !request.archivedAt)
    .toSorted((left, right) => (left.queueOrder - right.queueOrder) || Date.parse(left.createdAt) - Date.parse(right.createdAt)),
  [scopedRequests])
  const deletedRequests = useMemo(() => scopedRequests
    .filter((request) => request.deletedAt)
    .toSorted((left, right) => Date.parse(right.deletedAt ?? right.createdAt) - Date.parse(left.deletedAt ?? left.createdAt)),
  [scopedRequests])

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
            activeTab={activeTab}
            editingRequest={editingRequest}
            error={error}
            onTabChange={setActiveTab}
            onRefresh={onRefresh}
            onToggleFiles={onToggleFiles}
            onCreated={onCreated}
            onUpdateRequest={onUpdateRequest}
            onCancelEdit={() => setEditingRequest(null)}
            onUpdateProjectDefaults={onUpdateProjectDefaults}
            onError={onError}
          />
          {activeTab === 'queue' ? (
            <QueueList
              requests={queueRequests}
              selectedProject={selectedProject}
              diagnostics={diagnostics}
              now={now}
              onCancel={onCancel}
              onResume={onResume}
              onArchive={onArchiveRequest}
              onArchiveAll={onArchiveRequests}
              onEdit={(request) => setEditingRequest(request)}
              onReorder={(requestIds) => onReorderRequests(selectedProject.id, requestIds)}
              onDelete={onDeleteRequest}
              onKickQueue={onKickQueue}
            />
          ) : activeTab === 'history' ? (
            <RequestHistory requests={historyRequests} deletedRequests={deletedRequests} now={now} onDelete={onDeleteRequest} />
          ) : (
            <ProjectTerminal project={selectedProject} requests={queueRequests} now={now} onError={onError} />
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
          <GlassButton variant="danger" type="button" onClick={() => onRemove(project)}>
            <Trash2 size={15} /> Remove
          </GlassButton>
        </div>
      </form>
    </Modal>
  )
}

function QueueComposer({
  config,
  selectedProject,
  activeTab,
  editingRequest,
  error,
  onTabChange,
  onRefresh,
  onToggleFiles,
  onCreated,
  onUpdateRequest,
  onCancelEdit,
  onUpdateProjectDefaults,
  onError,
}: {
  config: ApiConfig
  selectedProject: Project
  activeTab: 'queue' | 'history' | 'terminal'
  editingRequest: CodexRequest | null
  error: string
  onTabChange: (tab: 'queue' | 'history' | 'terminal') => void
  onRefresh: () => Promise<void>
  onToggleFiles: () => void
  onCreated: () => Promise<void>
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
  const [attachments, setAttachments] = useState<QueueAttachment[]>([])
  const [attachmentError, setAttachmentError] = useState('')
  const [draggingFiles, setDraggingFiles] = useState(false)
  const [savingDefaults, setSavingDefaults] = useState(false)
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
    try {
      const payload: UpdateQueueRequest = {
        prompt,
        attachments: editingRequest && attachments.length === 0 ? undefined : attachments,
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
        await api.createRequest({
          projectId: selectedProject.id,
          ...payload,
          attachments,
        })
      }

      setPrompt('')
      setAttachments([])
      setAttachmentError('')
      resetModelSelections()
      await onCreated()
    } catch (cause) {
      onError(cause)
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
              <span className="meta">Existing attachments are kept unless you attach replacement files.</span>
            )}
            {attachments.length > 0 && (
              <div className="attachment-list">
                {attachments.map((attachment, index) => (
                  <div key={`${attachment.name}:${index}`} className="attachment-chip">
                    <span className="truncate">{attachment.name}</span>
                    <span>{formatBytes(attachment.size)}</span>
                    <button type="button" onClick={() => setAttachments((current) => current.filter((_, itemIndex) => itemIndex !== index))} aria-label={`Remove ${attachment.name}`}>
                      <X size={12} />
                    </button>
                  </div>
                ))}
              </div>
            )}
            {attachmentError && <span className="error-text">{attachmentError}</span>}
          </div>
        </FieldLabel>
        <div className="composer-grid compact">
          <ModelPicker label="Request" options={config.models} value={requestModel} onChange={setRequestModel} />
          <ModelPicker label="Commit" options={config.models} value={commitModel} onChange={setCommitModel} disabled={!generateCommit || !separateCommitSession} />
        </div>
        <div className="composer-actions-row">
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
          <div className="button-row">
            <GlassButton variant="secondary" size="sm" type="button" onClick={saveDefaults} disabled={!defaultsChanged || savingDefaults}>
              <Check size={13} /> {savingDefaults ? 'Saving' : 'Save defaults'}
            </GlassButton>
            <GlassButton variant="primary" type="submit" disabled={!prompt.trim() || !requestModel.model.trim()}>
              <Play size={16} /> {editingRequest ? 'Update' : 'Queue'}
            </GlassButton>
          </div>
        </div>
      </form>
    </GlassPanel>
  )
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

  return (
    <div className={`model-picker-grid ${disabled ? 'model-picker-grid--disabled' : ''}`} aria-disabled={disabled}>
      <div className="model-picker-head">
        <span className="model-picker-title">{label}</span>
        <GlassSelect
          value={selectedOption ? value.model : 'custom'}
          disabled={disabled}
          onChange={(event) => {
            if (event.target.value === 'custom') {
              onChange({ ...value, model: value.model || '' })
              return
            }
            const option = options.find((item) => item.model === event.target.value)
            if (option) {
              onChange({ model: option.model, effort: value.effort || 'medium', speed: option.supportsPriority ? value.speed : 'normal' })
            }
          }}
        >
          {options.map((option) => (
            <option key={option.model} value={option.model}>{option.label}</option>
          ))}
          <option value="custom">Custom</option>
        </GlassSelect>
      </div>
      {!selectedOption && (
        <GlassInput value={value.model} disabled={disabled} onChange={(event) => onChange({ ...value, model: event.target.value })} placeholder="model id" />
      )}
      <div className="model-options-row">
        <SegmentedRadio
          label="Intensity"
          name={`${label}-effort`}
          value={value.effort}
          disabled={disabled}
          options={[
            { label: 'Light', value: 'low' },
            { label: 'Medium', value: 'medium' },
            { label: 'High', value: 'high' },
            { label: 'XHigh', value: 'xhigh' },
          ]}
          onChange={(effort) => onChange({ ...value, effort })}
        />
        <SegmentedRadio
          label="Speed"
          name={`${label}-speed`}
          value={supportsPriority ? value.speed : 'normal'}
          disabled={disabled || !supportsPriority}
          options={[
            { label: 'Normal', value: 'normal' },
            { label: 'x1.5', value: 'priority' },
          ]}
          onChange={(speed) => onChange({ ...value, speed })}
        />
      </div>
    </div>
  )
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
          <label key={option.value} className={option.value === value ? 'active' : ''}>
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

function QueueList({
  requests,
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
  selectedProject?: Project
  diagnostics: QueueDiagnostics | null
  now: number
  onCancel: (id: string) => Promise<void>
  onResume: (id: string) => Promise<void>
  onArchive: (id: string) => Promise<void>
  onArchiveAll: (ids: string[]) => Promise<void>
  onEdit: (request: CodexRequest) => void
  onReorder: (requestIds: string[]) => Promise<void>
  onDelete: (id: string) => Promise<void>
  onKickQueue: () => Promise<void>
}) {
  const [selectedRequestId, setSelectedRequestId] = useState<string | null>(null)
  const [draggedRequestId, setDraggedRequestId] = useState<string | null>(null)
  const [dropTargetId, setDropTargetId] = useState<string | null>(null)
  const selectedRequest = requests.find((request) => request.id === selectedRequestId) ?? requests[0]
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

  const reorderQueuedRequests = async (targetRequestId: string) => {
    if (!draggedRequestId || draggedRequestId === targetRequestId) {
      setDraggedRequestId(null)
      setDropTargetId(null)
      return
    }

    const queuedIds = requests.filter((request) => request.status === 'Queued').map((request) => request.id)
    const fromIndex = queuedIds.indexOf(draggedRequestId)
    const toIndex = queuedIds.indexOf(targetRequestId)
    if (fromIndex === -1 || toIndex === -1) {
      setDraggedRequestId(null)
      setDropTargetId(null)
      return
    }

    const nextIds = [...queuedIds]
    const [movedId] = nextIds.splice(fromIndex, 1)
    nextIds.splice(toIndex, 0, movedId)
    setDraggedRequestId(null)
    setDropTargetId(null)
    await onReorder(nextIds)
  }

  return (
    <GlassPanel className="queue-panel">
      <div className="section-header">
        <h2>{selectedProject ? `${selectedProject.name} Queue` : 'Queue'}</h2>
        <div className="queue-header-actions">
          <span className="meta">{requests.length} requests</span>
          {succeededRequestIds.length > 0 && (
            <GlassButton variant="secondary" size="sm" type="button" onClick={() => onArchiveAll(succeededRequestIds)}>
              <Check size={13} /> Done all
            </GlassButton>
          )}
        </div>
      </div>
      {requests.some((request) => request.status === 'Queued' || request.status === 'Running') && (
        <QueueWorkerStatus diagnostics={diagnostics} onKickQueue={onKickQueue} />
      )}
      <div className="queue-workbench">
        <div className="request-list" aria-label="Queued requests">
          {requests.length === 0 && <span className="muted">No queued requests yet.</span>}
          {requests.map((request, index) => (
            <RequestCard
              key={request.id}
              request={request}
              now={now}
              queueNumber={index + 1}
              selected={request.id === selectedRequest?.id}
              onSelect={() => setSelectedRequestId(request.id)}
              onCancel={onCancel}
              onResume={onResume}
              onArchive={onArchive}
              onEdit={onEdit}
              onDelete={onDelete}
              dragging={request.id === draggedRequestId}
              dragOver={request.id === dropTargetId}
              onDragStart={() => {
                if (request.status === 'Queued') {
                  setDraggedRequestId(request.id)
                }
              }}
              onDragOver={(event) => {
                if (draggedRequestId && request.status === 'Queued') {
                  event.preventDefault()
                  setDropTargetId(request.id)
                }
              }}
              onDrop={() => {
                if (request.status === 'Queued') {
                  void reorderQueuedRequests(request.id)
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
  onDelete: (id: string) => Promise<void>
  dragging: boolean
  dragOver: boolean
  onDragStart: () => void
  onDragOver: (event: DragEvent<HTMLElement>) => void
  onDrop: () => void
  onDragEnd: () => void
}) {
  const cancellable = request.status === 'Queued' || request.status === 'Running' || request.status === 'CancelRequested' || request.status === 'UsageLimited'
  const resumable = request.status === 'Failed' || request.status === 'Cancelled' || request.status === 'UsageLimited'
  const archivable = request.status === 'Succeeded' && !request.archivedAt && !request.deletedAt
  const editable = request.status === 'Queued'
  const deletable = request.status !== 'Running' && request.status !== 'CancelRequested'
  const percent = progressFor(request)
  const requestUsageDelay = request.retryAfter ? formatRemainingTime(request.retryAfter, now) : null

  return (
    <article
      className={`request-card request-card--${request.status.toLowerCase()} ${selected ? 'active' : ''} ${dragging ? 'dragging' : ''} ${dragOver ? 'drag-over' : ''}`}
      role="button"
      tabIndex={0}
      draggable={editable}
      onClick={onSelect}
      onDragStart={onDragStart}
      onDragOver={onDragOver}
      onDrop={onDrop}
      onDragEnd={onDragEnd}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          onSelect()
        }
      }}
    >
      <div className="request-head">
        <div className={`queue-index ${editable ? 'queue-index--draggable' : ''}`} aria-label={`Queue position ${queueNumber}`}>
          {editable ? <GripVertical size={14} /> : queueNumber}
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
          <div className="meta truncate">{request.machineName} · created {formatDate(request.createdAt)}</div>
        </div>
        <div className="request-actions">
          {resumable && (
            <GlassButton
              variant="secondary"
              size="sm"
              onClick={(event) => {
                event.stopPropagation()
                onResume(request.id)
              }}
            >
              <Play size={13} /> Resume
            </GlassButton>
          )}
          {cancellable && (
            <GlassButton
              variant="danger"
              size="sm"
              onClick={(event) => {
                event.stopPropagation()
                onCancel(request.id)
              }}
            >
              <Square size={13} /> Cancel
            </GlassButton>
          )}
          {archivable && (
            <GlassButton
              variant="secondary"
              size="sm"
              onClick={(event) => {
                event.stopPropagation()
                onArchive(request.id)
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

function QueueWorkerStatus({
  diagnostics,
  onKickQueue,
}: {
  diagnostics: QueueDiagnostics | null
  onKickQueue: () => Promise<void>
}) {
  const message = diagnostics
    ? diagnostics.lastError
      ? `Worker error: ${diagnostics.lastError}`
      : diagnostics.isProcessing
        ? `Worker processing ${diagnostics.activeRequestIds.length || 1} request`
        : diagnostics.lastHeartbeat
          ? `Worker idle · last check ${formatDate(diagnostics.lastHeartbeat)}`
          : 'Worker has not reported yet'
    : 'Worker diagnostics unavailable until API is rebuilt'

  return (
    <div className={`queue-worker-status ${diagnostics?.lastError ? 'queue-worker-status--bad' : ''}`}>
      <span className="truncate">{message}</span>
      <GlassButton variant="secondary" size="sm" type="button" onClick={onKickQueue}>
        <Play size={13} /> Kick worker
      </GlassButton>
    </div>
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
}: {
  content?: string | null
  emptyText?: string
  forceExpanded?: boolean
}) {
  const parsed = useMemo(() => parseBody(content ?? ''), [content])
  const [expanded, setExpanded] = useState(false)
  const expandable = isBodyExpandable(parsed)
  const compact = expandable && !expanded && !forceExpanded

  useEffect(() => {
    setExpanded(false)
  }, [content])

  if (parsed.kind === 'empty') {
    return <div className="body-empty">{emptyText}</div>
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

  return <pre className={`log-block ${compact ? 'body-compact-block' : ''}`}>{parsed.text}</pre>
}

function BodyEventRow({ event, compact }: { event: BodyEvent, compact: boolean }) {
  return (
    <article className={`body-event ${compact ? 'body-event--compact' : ''}`}>
      <div className="body-event-head">
        <span className="body-event-type">{formatEventType(event.type)}</span>
        {event.status && <span className="body-event-status">{event.status}</span>}
        {typeof event.exitCode === 'number' && <span className="body-event-status">exit {event.exitCode}</span>}
      </div>
      {event.text && <p className="body-event-text">{event.text}</p>}
      {event.output && <pre className="body-event-output">{event.output}</pre>}
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

function parseBody(content: string): ParsedBody {
  const text = content.trim()
  if (!text) {
    return { kind: 'empty' }
  }

  const json = tryParseJson(text)
  if (typeof json === 'string') {
    return parseBody(json)
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

  return { kind: 'text', text: content }
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
  const text = stringValue(value.text)
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

function formatEventType(value: string) {
  return value
    .replace(/^item\./, '')
    .replace(/_/g, ' ')
    .replace(/\b\w/g, (letter) => letter.toUpperCase())
}

type CompletionFileChange = {
  path: string
  kind: string
}

function CompletionSummary({ request }: { request: CodexRequest }) {
  const commitRun = request.runs.find((run) => run.kind === 'Commit')
  const resultRun = commitRun ?? request.runs.find((run) => run.kind === 'Request')
  const fileChanges = extractFileChanges(request)
  const resultText = request.summary || lastUsefulText(resultRun?.output ?? '') || 'Completed successfully.'

  return (
    <section className="completion-summary" aria-label="Completion summary">
      <div className="completion-summary-head">
        <div>
          <div className="completion-title">Last result</div>
          <div className="completion-facts">
            <span>Finished {formatDate(request.finishedAt ?? request.createdAt)}</span>
            {commitRun?.commitSha && <span>Commit {commitRun.commitSha.slice(0, 12)}</span>}
            {fileChanges.length > 0 && <span>{fileChanges.length} files changed</span>}
          </div>
        </div>
        <Check size={18} />
      </div>
      <div className="completion-result-box">{resultText}</div>
      {commitRun?.commitMessage && <div className="completion-message">{commitRun.commitMessage}</div>}
      {fileChanges.length > 0 && (
        <div className="completion-changes">
          <div className="section-kicker">View changes</div>
          <div className="completion-files">
            {fileChanges.slice(0, 8).map((change, index) => (
              <div key={`${change.path}:${index}`} className="completion-file">
                <Code2 size={13} />
                <span className="truncate">{change.path}</span>
                <span>{change.kind}</span>
              </div>
            ))}
            {fileChanges.length > 8 && <div className="meta">+{fileChanges.length - 8} more files</div>}
          </div>
        </div>
      )}
    </section>
  )
}

function extractFileChanges(request: CodexRequest): CompletionFileChange[] {
  const changes = new Map<string, CompletionFileChange>()
  for (const run of request.runs) {
    for (const change of extractJsonFileChanges(run.output)) {
      changes.set(change.path, change)
    }

    for (const change of extractGitStatusChanges(run.output)) {
      changes.set(change.path, change)
    }
  }

  return Array.from(changes.values()).toSorted((left, right) => left.path.localeCompare(right.path))
}

function extractJsonFileChanges(output: string): CompletionFileChange[] {
  const changes: CompletionFileChange[] = []
  for (const line of output.split(/\r?\n/)) {
    const parsed = tryParseJson(line.trim())
    if (!isRecord(parsed)) continue
    const item = isRecord(parsed.item) ? parsed.item : parsed
    const source = Array.isArray(item.changes) ? item.changes : []
    for (const change of source.filter(isRecord)) {
      const path = stringValue(change.path)
      if (path) {
        changes.push({ path, kind: stringValue(change.kind) ?? stringValue(change.status) ?? 'changed' })
      }
    }
  }

  return changes
}

function extractGitStatusChanges(output: string): CompletionFileChange[] {
  const changes: CompletionFileChange[] = []
  for (const line of output.split(/\r?\n/)) {
    const match = line.match(/^\s*([MADRCU?]{1,2})\s+(.+)$/)
    if (!match) continue
    changes.push({ path: match[2].trim(), kind: gitStatusLabel(match[1].trim()) })
  }

  return changes
}

function gitStatusLabel(status: string) {
  if (status.includes('?')) return 'added'
  if (status.includes('D')) return 'deleted'
  if (status.includes('R')) return 'renamed'
  if (status.includes('A')) return 'added'
  if (status.includes('M')) return 'modified'
  return 'changed'
}

function lastUsefulText(output: string) {
  return output.split(/\r?\n/).map((line) => line.trim()).filter(Boolean).at(-1)
}

function QueueRequestDetails({ request, now }: { request?: CodexRequest; now: number }) {
  const commitRun = request?.runs.find((run) => run.kind === 'Commit')
  const requestRun = request?.runs.find((run) => run.kind === 'Request')

  if (!request) {
    return (
      <aside className="queue-detail-panel">
        <div className="empty-state">Select a queued request to inspect runs.</div>
      </aside>
    )
  }

  return (
    <aside className="queue-detail-panel" aria-label="Selected request details">
      <div className="queue-detail-header">
        <div className="queue-detail-heading">
          <div className="queue-detail-title truncate" title={request.prompt}>{requestDisplayName(request)}</div>
          <ModelChips model={request.model} effort={request.modelEffort} speed={request.modelSpeed} />
          <div className="meta truncate">{request.machineName} · {formatDate(request.createdAt)}</div>
        </div>
        <StatusBadge status={request.status} busy={request.status === 'Running'} />
      </div>
      <div className="queue-detail-body">
        <div className="section-kicker">Request body</div>
        {request.attachments.length > 0 && (
          <div className="request-attachments">
            {request.attachments.map((attachment, index) => (
              <span key={`${attachment.name}:${index}`} className="model-chip">
                {attachment.name} · {formatBytes(attachment.size)}
              </span>
            ))}
          </div>
        )}
        <div className="request-body-scroll">
          <StructuredBodyView content={request.prompt} forceExpanded />
        </div>
      </div>
      {request.status === 'Succeeded' && <CompletionSummary request={request} />}
      {request.runs.map((run) => (
        <div key={run.id} className="run-detail-card">
          <div className="row-between">
            <div className="run-title-stack">
              <strong>{run.kind}</strong>
              <ModelChips model={run.model} effort={run.modelEffort} speed={run.modelSpeed} />
            </div>
            <StatusBadge status={run.status} busy={run.status === 'Running'} />
          </div>
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
          <StructuredBodyView content={run.output} emptyText="No output yet." />
        </div>
      ))}
      {request.generateCommit && request.separateCommitSession && !commitRun && (
        <div className="pending-run-row">
          <div className="run-title-stack">
            <strong>Commit</strong>
            <ModelChips model={request.commitModel || request.model} effort={request.commitModelEffort || request.modelEffort} speed={request.commitModelSpeed || request.modelSpeed} />
          </div>
          <StatusBadge status="Queued" busy={requestRun?.status === 'Succeeded'} />
        </div>
      )}
    </aside>
  )
}

function ProjectTerminal({
  project,
  requests,
  now,
  onError,
}: {
  project: Project
  requests: CodexRequest[]
  now: number
  onError: (cause: unknown) => void
}) {
  const [command, setCommand] = useState('')
  const [terminalOutput, setTerminalOutput] = useState('')
  const [socketStatus, setSocketStatus] = useState<'connecting' | 'connected' | 'closed'>('connecting')
  const [commandHistory, setCommandHistory] = useState<string[]>([])
  const [historyCursor, setHistoryCursor] = useState<number | null>(null)
  const screenRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const socketRef = useRef<WebSocket | null>(null)
  const queuedCount = requests.filter((request) => request.status === 'Queued').length
  const runningCount = requests.filter((request) => request.status === 'Running').length
  const limitedRequest = requests.find((request) => request.status === 'UsageLimited')
  const limitedRemaining = limitedRequest?.retryAfter ? formatRemainingTime(limitedRequest.retryAfter, now) : null
  const promptPath = terminalPathLabel(project.path)

  useEffect(() => {
    screenRef.current?.scrollTo({ top: screenRef.current.scrollHeight })
  }, [terminalOutput, command])

  useEffect(() => {
    setTerminalOutput('')
    setSocketStatus('connecting')
    const socket = new WebSocket(apiWebSocketUrl(`/projects/${project.id}/terminal/ws`))
    socketRef.current = socket
    socket.onopen = () => {
      setSocketStatus('connected')
      inputRef.current?.focus()
    }
    socket.onmessage = (event) => {
      setTerminalOutput((current) => current + String(event.data))
    }
    socket.onerror = () => {
      setSocketStatus('closed')
      setTerminalOutput((current) => current + '\n[terminal disconnected]\n')
    }
    socket.onclose = () => {
      setSocketStatus('closed')
    }

    return () => {
      socket.close()
      socketRef.current = null
    }
  }, [project.id])

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    const trimmed = command.trim()
    if (!trimmed) return

    setCommand('')
    setCommandHistory((current) => (current.at(-1) === trimmed ? current : [...current, trimmed].slice(-100)))
    setHistoryCursor(null)
    if (socketRef.current?.readyState !== WebSocket.OPEN) {
      const message = 'Terminal is not connected.'
      setTerminalOutput((current) => current + `\n${message}\n`)
      onError(new Error(message))
      return
    }

    setTerminalOutput((current) => current + `${project.machineName}:${promptPath}$ ${trimmed}\n`)
    socketRef.current.send(`${trimmed}\n`)
  }

  const handleTerminalKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.ctrlKey && event.key.toLowerCase() === 'l') {
      event.preventDefault()
      setTerminalOutput('')
      return
    }

    if (event.key === 'ArrowUp') {
      event.preventDefault()
      if (commandHistory.length === 0) return
      const nextCursor = historyCursor === null ? commandHistory.length - 1 : Math.max(0, historyCursor - 1)
      setHistoryCursor(nextCursor)
      setCommand(commandHistory[nextCursor] ?? '')
      return
    }

    if (event.key === 'ArrowDown') {
      event.preventDefault()
      if (commandHistory.length === 0 || historyCursor === null) return
      const nextCursor = historyCursor + 1
      if (nextCursor >= commandHistory.length) {
        setHistoryCursor(null)
        setCommand('')
        return
      }

      setHistoryCursor(nextCursor)
      setCommand(commandHistory[nextCursor] ?? '')
    }
  }

  return (
    <GlassPanel className="terminal-panel">
      <div className="terminal-status-strip" aria-label="Queue status">
        <span><strong>{queuedCount}</strong> queued</span>
        <span><strong>{runningCount}</strong> running</span>
        <span className={limitedRequest ? 'terminal-status-warn' : ''}>
          <strong>{limitedRequest ? 'Limited' : 'Ready'}</strong>
          {limitedRequest ? ` ${limitedRemaining ? `for ${limitedRemaining}` : 'retry pending'}` : ' usage'}
        </span>
        <span className={socketStatus === 'connected' ? 'terminal-status-ok' : ''}>
          <strong>{socketStatus}</strong> shell
        </span>
      </div>
      <form className="terminal-form" onSubmit={submit}>
        <div
          ref={screenRef}
          className="terminal-screen"
          aria-live="polite"
          onClick={() => inputRef.current?.focus()}
        >
          {!terminalOutput && (
            <div className="terminal-empty">
              <div>Codex Queue terminal</div>
              <div>{project.machineName}:{project.path}</div>
            </div>
          )}
          <TerminalOutput output={terminalOutput} success />
          <div className="terminal-live-line">
            <TerminalPrompt machineName={project.machineName} path={promptPath} />
            <input
              ref={inputRef}
              className="terminal-inline-input"
              value={command}
              onChange={(event) => {
                setCommand(event.target.value)
                setHistoryCursor(null)
              }}
              onKeyDown={handleTerminalKeyDown}
              readOnly={socketStatus !== 'connected'}
              spellCheck={false}
              autoCapitalize="off"
              autoComplete="off"
              aria-label="Terminal command"
              autoFocus
            />
            <span className="terminal-cursor" aria-hidden="true" />
          </div>
        </div>
      </form>
    </GlassPanel>
  )
}

function TerminalPrompt({ machineName, path }: { machineName: string; path: string }) {
  return (
    <span className="terminal-prompt" aria-hidden="true">
      <span className="terminal-prompt-user">{machineName}</span>
      <span className="terminal-prompt-separator">:</span>
      <span className="terminal-prompt-path">{path}</span>
      <span className="terminal-prompt-symbol">$</span>
    </span>
  )
}

function TerminalOutput({ output, success }: { output: string; success: boolean }) {
  return (
    <pre className={`terminal-output ${success ? 'terminal-output--ok' : 'terminal-output--bad'}`}>
      {parseAnsiOutput(output).map((segment, index) => (
        <span key={index} className={segment.className}>{segment.text}</span>
      ))}
    </pre>
  )
}

function terminalPathLabel(path: string) {
  const normalized = path.replace(/\\/g, '/').replace(/\/+$/, '')
  const name = normalized.split('/').filter(Boolean).at(-1)
  return name ? `~/${name}` : '~'
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
  const requestRun = request.runs.find((run) => run.kind === 'Request')
  const commitRun = request.runs.find((run) => run.kind === 'Commit')
  if (commitRun?.status === 'Running') return 82
  if (requestRun?.status === 'Succeeded' && request.generateCommit && request.separateCommitSession) return 68
  return 42
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
  onDelete: (id: string) => Promise<void>
}) {
  const [showTrash, setShowTrash] = useState(false)
  const [selectedRequestId, setSelectedRequestId] = useState<string | null>(null)
  const visibleRequests = showTrash ? deletedRequests : requests
  const selectedRequest = visibleRequests.find((request) => request.id === selectedRequestId) ?? visibleRequests[0]

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
            const commitRun = request.runs.find((run) => run.kind === 'Commit' && run.commitSha)
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
  selectedProject,
  onOpenFile,
  onClose,
  onError,
}: {
  selectedProject?: Project
  onOpenFile: (project: Project, path: string) => Promise<void>
  onClose: () => void
  onError: (cause: unknown) => void
}) {
  return (
    <aside className="right-rail">
      <div className="section-header">
        <h2>Files</h2>
        <GlassButton variant="ghost" size="icon" onClick={onClose} title="Close files">
          <X size={16} />
        </GlassButton>
      </div>
      {selectedProject ? (
        <DirectoryTree project={selectedProject} onOpenFile={onOpenFile} onError={onError} />
      ) : (
        <span className="muted">Select a project to browse files.</span>
      )}
    </aside>
  )
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
    <div className="section-stack">
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

function ModelChips({ model, effort, speed }: { model: string, effort?: string | null, speed?: string | null }) {
  const effortLabels: Record<string, string> = {
    low: 'light',
    medium: 'medium',
    high: 'high',
    xhigh: 'xhigh',
  }
  const normalizedSpeed = speed === 'priority' ? 'x1.5' : speed || 'normal'

  return (
    <div className="model-chip-row" aria-label="Selected model settings">
      <span className="model-chip model-chip--model">{model}</span>
      {effort && <span className="model-chip">{effortLabels[effort] ?? effort}</span>}
      <span className={`model-chip model-chip--speed ${speed === 'priority' ? 'model-chip--speed-priority' : ''}`}>{normalizedSpeed}</span>
    </div>
  )
}

export default App
