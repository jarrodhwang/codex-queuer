export type MachineKind = 'Local' | 'Ssh'
export type MachinePlatform = 'Auto' | 'Linux' | 'MacOs' | 'Windows'
export type QueueStatus =
  | 'Queued'
  | 'Running'
  | 'UsageLimited'
  | 'Succeeded'
  | 'Failed'
  | 'CancelRequested'
  | 'Cancelled'

export type RunKind = 'Request' | 'Commit'

export type Machine = {
  id: string
  name: string
  kind: MachineKind
  host?: string | null
  port: number
  userName?: string | null
  sshKeyPath?: string | null
  workingRoot?: string | null
  platform: MachinePlatform
  createdAt: string
  updatedAt: string
}

export type SaveMachineRequest = {
  name: string
  kind: MachineKind
  host?: string | null
  port?: number | null
  userName?: string | null
  sshKeyPath?: string | null
  workingRoot?: string | null
  platform?: MachinePlatform | null
}

export type Project = {
  id: string
  name: string
  path: string
  machineId: string
  machineName: string
  machineKind: MachineKind
  defaultModel?: string | null
  defaultModelEffort?: string | null
  defaultModelSpeed?: string | null
  defaultCommitModel?: string | null
  defaultCommitModelEffort?: string | null
  defaultCommitModelSpeed?: string | null
  defaultGenerateCommit?: boolean | null
  defaultSeparateCommitSession?: boolean | null
  createdAt: string
  updatedAt: string
}

export type SaveProjectRequest = {
  name: string
  path: string
  machineId: string
  defaultModel?: string | null
  defaultModelEffort?: string | null
  defaultModelSpeed?: string | null
  defaultCommitModel?: string | null
  defaultCommitModelEffort?: string | null
  defaultCommitModelSpeed?: string | null
  defaultGenerateCommit?: boolean | null
  defaultSeparateCommitSession?: boolean | null
}

export type QueueTab = {
  id: string
  projectId: string
  name: string
  createdAt: string
  updatedAt: string
}

export type CreateQueueRequest = {
  projectId: string
  queueTabId?: string | null
  prompt: string
  attachments?: QueueAttachment[]
  model: string
  modelEffort?: string | null
  modelSpeed?: string | null
  generateCommit: boolean
  separateCommitSession: boolean
  commitModel?: string | null
  commitModelEffort?: string | null
  commitModelSpeed?: string | null
}

export type UpdateQueueRequest = Omit<CreateQueueRequest, 'projectId'>

export type QueueAttachment = {
  name: string
  contentType: string
  size: number
  contentBase64: string
}

export type CodexRun = {
  id: string
  kind: RunKind
  model: string
  modelEffort?: string | null
  modelSpeed?: string | null
  status: QueueStatus
  retryAfter?: string | null
  retryReason?: string | null
  availableModel?: string | null
  commandPreview?: string | null
  output: string
  exitCode?: number | null
  commitMessage?: string | null
  commitSha?: string | null
  error?: string | null
  createdAt: string
  startedAt?: string | null
  finishedAt?: string | null
}

export type CodexRequest = {
  id: string
  projectId: string
  queueTabId?: string | null
  queueTabName?: string | null
  projectName: string
  projectPath: string
  machineId: string
  machineName: string
  machineKind: MachineKind
  prompt: string
  attachments: Array<{ name: string, contentType: string, size: number }>
  model: string
  modelEffort?: string | null
  modelSpeed?: string | null
  queueOrder: number
  status: QueueStatus
  generateCommit: boolean
  separateCommitSession: boolean
  retryAfter?: string | null
  retryReason?: string | null
  availableModel?: string | null
  commitModel?: string | null
  commitModelEffort?: string | null
  commitModelSpeed?: string | null
  summary?: string | null
  error?: string | null
  createdAt: string
  startedAt?: string | null
  finishedAt?: string | null
  archivedAt?: string | null
  deletedAt?: string | null
  runs: CodexRun[]
}

export type Session = {
  runId: string
  requestId: string
  projectName: string
  machineName: string
  kind: RunKind
  model: string
  status: QueueStatus
  createdAt: string
  startedAt?: string | null
  finishedAt?: string | null
  commitSha?: string | null
}

export type FileTreeEntry = {
  name: string
  path: string
  isDirectory: boolean
  size?: number | null
}

export type FileContent = {
  path: string
  content: string
  size: number
  truncated: boolean
}

export type TerminalCommandResult = {
  success: boolean
  output: string
  exitCode: number
  commandPreview: string
}

export type GitFileChange = {
  path: string
  status: string
  staged: boolean
  unstaged: boolean
}

export type GitStatus = {
  branch: string
  isClean: boolean
  changes: GitFileChange[]
  diffStat: string
  output: string
}

export type GitCommitRequest = {
  message: string
}

export type GitCommitResult = {
  success: boolean
  output: string
  exitCode: number
  commandPreview: string
  commitSha?: string | null
}

export type CodexGitCommitRequest = {
  model: string
  modelEffort?: string | null
  modelSpeed?: string | null
}

export type SuggestGitCommitMessageRequest = {
  model: string
  modelEffort?: string | null
  modelSpeed?: string | null
}

export type SuggestGitCommitMessageResult = {
  message: string
  output: string
}

export type ModelOption = {
  label: string
  model: string
  supportsPriority: boolean
}

export type ApiConfig = {
  requiresToken: boolean
  models: ModelOption[]
}

export type MachineTest = {
  success: boolean
  output: string
}

export type RateLimitWindow = {
  usedPercent: number
  windowDurationMins?: number | null
  resetsAt?: number | null
}

export type MachineRateLimits = {
  machineId: string
  machineName: string
  available: boolean
  error?: string | null
  limits: RateLimit[]
}

export type RateLimit = {
  id: string
  name: string
  primary?: RateLimitWindow | null
  secondary?: RateLimitWindow | null
  rateLimitReachedType?: string | null
}

export type QueueDiagnostics = {
  lastHeartbeat?: string | null
  lastDispatch?: string | null
  lastIdle?: string | null
  lastError?: string | null
  activeRequestIds: string[]
  isProcessing: boolean
}
