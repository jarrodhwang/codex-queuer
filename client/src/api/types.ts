export type MachineKind = 'Local' | 'Ssh'
export type MachinePlatform = 'Auto' | 'Linux' | 'MacOs' | 'Windows'
export type ExecutionRunner = 'CodexCli' | 'OpenHandsCli'
export type AiProviderSource = 'OpenAi' | 'Anthropic' | 'Local'
export type ModelDiscoveryMode = 'Auto' | 'Ollama' | 'OpenAi'
export type LocalAiServerType = 'Ollama' | 'LmStudio' | 'LlamaCpp'
export type ProviderHealthStatus = 'Unknown' | 'Healthy' | 'Offline'
export type QueueStatus =
  | 'Queued'
  | 'Running'
  | 'UsageLimited'
  | 'Succeeded'
  | 'Failed'
  | 'CancelRequested'
  | 'Cancelled'

export type RunKind = 'Request' | 'Commit'
export type PermissionMode = 'ReadOnly' | 'AskForApproval' | 'ApproveForMe' | 'FullAccess'

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
  defaultPermissionMode?: PermissionMode | null
  defaultInternetSearchEnabled?: boolean | null
  defaultCommitExecutionRunner?: ExecutionRunner | null
  defaultCommitLocalProviderProfileId?: string | null
  defaultExecutionRunner?: ExecutionRunner | null
  defaultLocalProviderProfileId?: string | null
  defaultLocalModel?: string | null
  defaultLocalModelEffort?: string | null
  defaultLocalModelSpeed?: string | null
  separateQueuesByTab: boolean
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
  defaultPermissionMode?: PermissionMode | null
  defaultInternetSearchEnabled?: boolean | null
  defaultCommitExecutionRunner?: ExecutionRunner | null
  defaultCommitLocalProviderProfileId?: string | null
  defaultExecutionRunner?: ExecutionRunner | null
  defaultLocalProviderProfileId?: string | null
  defaultLocalModel?: string | null
  defaultLocalModelEffort?: string | null
  defaultLocalModelSpeed?: string | null
  separateQueuesByTab?: boolean | null
}

export type QueueTab = {
  id: string
  projectId: string
  name: string
  openHandsConversationId?: string | null
  localCodexSessionId?: string | null
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
  permissionMode: PermissionMode
  internetSearchEnabled?: boolean
  commitExecutionRunner?: ExecutionRunner | null
  commitProviderProfileId?: string | null
  executionRunner?: ExecutionRunner
  providerProfileId?: string | null
  openHandsAlwaysApproveConfirmed?: boolean
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
  executionRunner?: ExecutionRunner
  providerProfileId?: string | null
  providerProfileName?: string | null
  providerSource?: AiProviderSource | null
  openHandsConversationId?: string | null
  localCodexSessionId?: string | null
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
  permissionMode: PermissionMode
  internetSearchEnabled?: boolean
  commitExecutionRunner?: ExecutionRunner | null
  commitProviderProfileId?: string | null
  executionRunner?: ExecutionRunner
  providerProfileId?: string | null
  providerProfileName?: string | null
  providerSource?: AiProviderSource | null
  queueWaitReason?: string | null
  openHandsAlwaysApproveConfirmed?: boolean
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
  executionRunner?: ExecutionRunner
  providerProfileName?: string | null
  providerSource?: AiProviderSource | null
  openHandsConversationId?: string | null
  localCodexSessionId?: string | null
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
  executionRunner?: ExecutionRunner | null
  providerProfileId?: string | null
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

export type AiProviderProfile = {
  id: string
  name: string
  source: AiProviderSource
  localAiServerType: LocalAiServerType
  baseUrl: string
  modelDiscoveryMode: ModelDiscoveryMode
  apiKeyEnvironmentVariable?: string | null
  enabled: boolean
  maximumConcurrency: number
  configuredContextWindow?: number | null
  defaultModel?: string | null
  serverMachineId?: string | null
  lastHealthStatus: ProviderHealthStatus
  lastHealthAt?: string | null
  lastHealthError?: string | null
  createdAt: string
  updatedAt: string
}

export type SaveAiProviderProfileRequest = {
  name: string
  source: AiProviderSource
  localAiServerType: LocalAiServerType
  baseUrl: string
  modelDiscoveryMode: ModelDiscoveryMode
  apiKeyEnvironmentVariable?: string | null
  enabled: boolean
  maximumConcurrency: number
  configuredContextWindow?: number | null
  defaultModel?: string | null
  serverMachineId?: string | null
}

export type ProviderModel = {
  id: string
  name: string
  maximumContextWindow?: number | null
  supportsTools: boolean
  supportsReasoning: boolean
  supportsReasoningEffort: boolean
  toolSupportKnown: boolean
}

export type ProviderModelsResponse = {
  profileId: string
  healthy: boolean
  status: ProviderHealthStatus
  error?: string | null
  checkedAt: string
  configuredContextWindow?: number | null
  contextWarning?: string | null
  models: ProviderModel[]
}

export type MachineTest = {
  success: boolean
  output: string
}

export type LocalCodexMachineTest = {
  available: boolean
  version?: string | null
  requiresWsl: boolean
  message: string
  targetLocalAiChecked?: boolean
  targetLocalAiReachable?: boolean | null
  targetSelectedModelAvailable?: boolean | null
  targetLocalAiMessage?: string | null
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

export type MachineGpuResource = {
  index: number
  name: string
  utilizationPercent?: number | null
  memoryUsagePercent?: number | null
  memoryUsedBytes?: number | null
  memoryTotalBytes?: number | null
  temperatureCelsius?: number | null
  powerWatts?: number | null
}

export type MachineResources = {
  machineId: string
  machineName: string
  available: boolean
  error?: string | null
  cpuUsagePercent?: number | null
  memoryUsagePercent?: number | null
  memoryUsedBytes?: number | null
  memoryTotalBytes?: number | null
  cpuTemperatureCelsius?: number | null
  systemTemperatureCelsius?: number | null
  systemPowerWatts?: number | null
  systemPowerSource?: string | null
  gpus: MachineGpuResource[]
  collectedAt: string
  cpuName?: string | null
  memoryName?: string | null
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
