namespace HyperVPanel.Services;

public sealed record UserVmPermissions(
    bool CanViewConsole,
    bool CanViewResources,
    bool CanViewAccess,
    bool CanViewNetwork,
    bool CanViewBackups,
    bool CanRequestReinstall,
    bool CanViewActivity,
    bool CanViewSettings,
    bool CanUsePowerActions)
{
    public static UserVmPermissions Admin => new(true, true, true, true, true, true, true, true, true);

    public static UserVmPermissions ClientDefault => new(
        CanViewConsole: true,
        CanViewResources: true,
        CanViewAccess: true,
        CanViewNetwork: false,
        CanViewBackups: false,
        CanRequestReinstall: true,
        CanViewActivity: true,
        CanViewSettings: false,
        CanUsePowerActions: true);

    public bool CanViewTab(string tab) => tab switch
    {
        "console" => CanViewConsole,
        "resources" => CanViewResources,
        "monitoring" => CanViewResources,
        "health" => CanViewSettings,
        "access" => CanViewAccess,
        "recovery" => CanViewAccess,
        "network" => CanViewNetwork,
        "firewall" => CanViewNetwork,
        "backups" => CanViewBackups,
        "reinstall" => CanRequestReinstall,
        "activity" => CanViewActivity,
        "settings" => CanViewSettings,
        _ => false
    };
}

public sealed record PanelUser(long Id, string Username, string Role, UserVmPermissions Permissions, long? ParentUserId = null, bool MustChangePassword = false)
{
    public bool IsAdmin => string.Equals(Role, "admin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Username, "admin", StringComparison.OrdinalIgnoreCase);

    public UserVmPermissions EffectivePermissions => IsAdmin ? UserVmPermissions.Admin : Permissions;

    public bool IsSubUser => ParentUserId is not null;
}

public sealed record PanelUserSummary(
    long Id,
    string Username,
    string Role,
    string Email,
    DateTime CreatedAt,
    int AssignedVmCount,
    UserVmPermissions Permissions,
    long? ParentUserId = null,
    string ParentUsername = "",
    bool MustChangePassword = false)
{
    public bool IsAdmin => string.Equals(Role, "admin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Username, "admin", StringComparison.OrdinalIgnoreCase);

    public UserVmPermissions EffectivePermissions => IsAdmin ? UserVmPermissions.Admin : Permissions;

    public bool IsSubUser => ParentUserId is not null;
}

public sealed record PasswordRecoveryUser(
    long Id,
    string Username,
    string Role,
    string Email);

public sealed record UserVmAssignment(
    long Id,
    long UserId,
    string Username,
    string VmName,
    DateTime CreatedAt);

public sealed record VmInfo(
    string Name,
    string State,
    int CpuUsage,
    long MemoryAssigned,
    TimeSpan Uptime,
    string Status);

public sealed record VmAccessInfo(
    string VmName,
    string Hostname,
    string IpAddress,
    int? RdpPort,
    int? SshPort,
    string Username,
    string Password,
    string Notes,
    string WebConsoleUrl,
    DateTime UpdatedAt);

public sealed record VmCheckpointInfo(
    string Name,
    string Id,
    string SnapshotType,
    DateTime CreationTime,
    long DiskFootprintBytes);

public sealed record VmQuickSnapshotMetadata(
    string VmName,
    string SnapshotId,
    string SnapshotName,
    string Description,
    bool IsPinned,
    bool IsTemporarySafety,
    DateTime? ExpiresAt,
    long? CreatedByUserId,
    string CreatedByUsername,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record VmQuickRestorePointInfo(
    string Name,
    string Id,
    string SnapshotType,
    DateTime CreationTime,
    long DiskFootprintBytes,
    string Description,
    bool IsPinned,
    bool IsTemporarySafety,
    DateTime? ExpiresAt,
    bool IsExpired);

public sealed record VmQuickSnapshotStorageStatus(
    int RestorePointCount,
    int ManualRestorePointCount,
    int TemporarySafetyCount,
    int ExpiredUnpinnedCount,
    int MaxRestorePointsPerVm,
    int RecommendedMaxAgeDays,
    int SafetyRetentionHours,
    long TotalAvhdxBytes,
    long WarningStorageBytes,
    bool StorageWarning);

public sealed record QuickRestorePointRestoreResult(
    string VmName,
    string RestorePointName,
    string SafetyRestorePointName,
    bool WasRunning,
    string PreviousState,
    string FinalState,
    string Message);

public sealed record VmNetworkPortProfile(
    string VmName,
    string NatName,
    string InternalIpAddress,
    string PublicIpAddress,
    string PublicHostname,
    bool EnableRdp,
    int? RdpExternalPort,
    int RdpInternalPort,
    bool EnableFiveM,
    int? FiveMExternalPort,
    int FiveMInternalPort,
    string ExtraTcpPorts,
    string ExtraUdpPorts,
    string DnsInterfaceAlias,
    string PreferredDnsServer,
    string AlternateDnsServer,
    DateTime UpdatedAt)
{
    public bool HasRdpRule => EnableRdp && RdpExternalPort is > 0 && RdpInternalPort > 0 && !string.IsNullOrWhiteSpace(InternalIpAddress);
    public bool HasFiveMRule => EnableFiveM && FiveMExternalPort is > 0 && FiveMInternalPort > 0 && !string.IsNullOrWhiteSpace(InternalIpAddress);
    public bool HasGuestDnsSettings => !string.IsNullOrWhiteSpace(PreferredDnsServer) || !string.IsNullOrWhiteSpace(AlternateDnsServer);
}

public sealed record NetworkProfileApplyResult(
    string VmName,
    int RulesApplied,
    string Message);

public sealed record NetworkProfileCleanupResult(
    string VmName,
    int NatMappingsRemoved,
    int HostFirewallRulesRemoved,
    string Message);

public sealed record VmDeleteResult(
    string VmName,
    string VmPath,
    int NatMappingsRemoved,
    int HostFirewallRulesRemoved,
    bool StorageDeleted,
    string Message);

public sealed record VmGuestFirewallRule(
    string Name,
    string DisplayName,
    string Group,
    string Protocol,
    string LocalPort,
    string RemoteAddress,
    string Action,
    bool Enabled,
    bool IsAutomatic);

public sealed record GuestFirewallApplyResult(
    string VmName,
    int RulesApplied,
    string Message);


public sealed record GuestNetworkRepairResult(
    string VmName,
    string InterfaceAlias,
    string IpAddress,
    string Gateway,
    string DnsServers,
    bool DnsResolved,
    bool PanelReachable,
    string Message);

public sealed record VmGuestDnsStatus(
    string VmName,
    string InterfaceAlias,
    IReadOnlyList<string> DnsServers,
    DateTime CheckedAt);

public sealed record VmRdpListenerStatus(
    string VmName,
    int Port,
    bool RemoteDesktopEnabled,
    bool TermServiceRunning,
    bool TcpListening,
    bool UdpListening,
    DateTime CheckedAt)
{
    public bool IsReady => RemoteDesktopEnabled && TermServiceRunning && TcpListening;
    public bool IsUdpReady => RemoteDesktopEnabled && TermServiceRunning && UdpListening;
}

public sealed record HyperVConsoleFrame(
    string VmName,
    string DataUri,
    int Width,
    int Height,
    int VirtualWidth,
    int VirtualHeight,
    DateTime CapturedAt);

public sealed record VmHostPortHealth(
    string Label,
    string Protocol,
    int ExternalPort,
    int InternalPort,
    bool NatHealthy,
    bool HostFirewallHealthy,
    string NatDetails,
    string HostFirewallDetails);

public sealed record VmHostInfrastructureHealth(
    string VmName,
    bool VmExists,
    string VmState,
    string DiskPath,
    bool DiskExists,
    string NatName,
    bool NatExists,
    string ExpectedInternalIp,
    IReadOnlyList<string> HyperVReportedIps,
    bool InternalIpObserved,
    IReadOnlyList<VmHostPortHealth> Ports,
    DateTime CheckedAt);

public sealed record VmHealthCheck(
    string Key,
    string Name,
    string Status,
    string Summary,
    string Details,
    bool Repairable);

public sealed record VmHealthReport(
    string VmName,
    string OverallStatus,
    IReadOnlyList<VmHealthCheck> Checks,
    DateTime CheckedAt);

public sealed record VmHealthRepairResult(
    string VmName,
    int RepairActions,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Warnings,
    VmHealthReport Report,
    string Message);

public sealed record ConsistencyHostVm(
    string Name,
    string State,
    string VmPath,
    IReadOnlyList<string> DiskPaths);

public sealed record ConsistencyNatMapping(
    string NatName,
    string Protocol,
    string ExternalIpAddress,
    int ExternalPort,
    string InternalIpAddress,
    int InternalPort);

public sealed record ConsistencyFirewallRule(
    string Name,
    string DisplayName,
    string Group,
    string Protocol,
    string LocalPort,
    string Direction,
    string Action,
    bool Enabled);

public sealed record ConsistencyStorageDisk(
    string Root,
    string Path,
    long SizeBytes);

public sealed record ConsistencyHostInventory(
    string NatName,
    bool NatExists,
    IReadOnlyList<ConsistencyHostVm> Vms,
    IReadOnlyList<ConsistencyNatMapping> NatMappings,
    IReadOnlyList<ConsistencyFirewallRule> ManagedFirewallRules,
    IReadOnlyList<ConsistencyStorageDisk> StorageDisks,
    DateTime CheckedAt);

public sealed record VmRecoveryInventory(
    string VmName,
    bool IsEnabled,
    string Username,
    bool HasSecret,
    string LastError,
    DateTime UpdatedAt);

public sealed record ConsistencyIssue(
    string Key,
    string Severity,
    string Category,
    string Resource,
    string Summary,
    string Details,
    string SuggestedAction,
    bool SafeCleanupCandidate);

public sealed record ConsistencyScanReport(
    string OverallStatus,
    int HyperVVmCount,
    int ProfileCount,
    int WhmcsServiceCount,
    int NatMappingCount,
    int ManagedFirewallRuleCount,
    int StorageDiskCount,
    int CriticalCount,
    int WarningCount,
    int ReviewCount,
    int SafeCleanupCandidateCount,
    IReadOnlyList<ConsistencyIssue> Issues,
    DateTime ScannedAt);

public sealed record ConsistencyCleanupAction(
    string ActionKey,
    string Kind,
    string Category,
    string Resource,
    string Summary,
    string Details,
    string VmName,
    string Protocol,
    int ExternalPort,
    string InternalIpAddress,
    int InternalPort,
    string FirewallRuleName,
    string FirewallDisplayName,
    long RecordId);

public sealed record ConsistencyCleanupPlan(
    IReadOnlyList<ConsistencyCleanupAction> Actions,
    DateTime GeneratedAt);

public sealed record ConsistencyCleanupExecutionItem(
    string ActionKey,
    string Resource,
    string Status,
    string Message);

public sealed record ConsistencyCleanupResult(
    int AppliedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<ConsistencyCleanupExecutionItem> Items,
    ConsistencyScanReport Report,
    DateTime CompletedAt);

public sealed record VmTemplate(
    long Id,
    string Name,
    string OperatingSystem,
    string IsoPath,
    string Notes,
    bool IsEnabled,
    DateTime CreatedAt);

public sealed record VmTemplateBootstrapCredential(
    long TemplateId,
    string Username,
    string Password);

public sealed record VmTemplateBootstrapStatus(
    long TemplateId,
    string Username,
    bool HasPassword);

public sealed record VmTemplateValidationStatus(
    long TemplateId,
    int VmGeneration,
    string Status,
    string Message,
    string WindowsProductName,
    string WindowsVersion,
    string BuildNumber,
    string Architecture,
    bool PowerShellDirectOk,
    bool RdpServiceFound,
    bool RdpEnabled,
    string RdpServiceStatus,
    long VirtualDiskBytes,
    long PhysicalDiskBytes,
    long TemplateLastWriteUtcTicks,
    DateTime? CheckedAt,
    bool IsCurrent);

public sealed record VmTemplateValidationSandbox(
    string VmName,
    string WorkingDirectory,
    string DifferencingDiskPath,
    long VirtualDiskBytes,
    long PhysicalDiskBytes,
    long TemplateLastWriteUtcTicks,
    int Generation);

public sealed record VmTemplateGuestValidationInfo(
    string ComputerName,
    string WindowsProductName,
    string WindowsVersion,
    string BuildNumber,
    string Architecture,
    bool RdpServiceFound,
    bool RdpEnabled,
    string RdpServiceStatus);

public sealed record VmStorageRootStatus(
    string Path,
    bool IsDefault,
    bool IsAvailable,
    long FreeBytes,
    long TotalBytes,
    string Error);

public sealed record VmProvisioningDefaults(
    string VmStorageRoot,
    IReadOnlyList<string> VmStorageRoots,
    string SwitchName,
    string NatName,
    string PublicAddress,
    string InternalIpStart,
    string InternalIpEnd,
    string Gateway,
    string PreferredDnsServer,
    string AlternateDnsServer,
    int RdpPortStart,
    int RdpPortEnd,
    int FiveMPortStart,
    int FiveMPortEnd,
    int DefaultFiveMInternalPort,
    int DefaultGeneration,
    bool DefaultSecureBoot);

public sealed record VmProvisionRequest(
    long UserId,
    string VmName,
    long TemplateId,
    int CpuCores,
    int MemoryMb,
    int DiskGb,
    int Generation,
    bool SecureBoot,
    bool StartAfterProvision,
    bool ConfigureWindowsGuest,
    string GuestUsername,
    string TemplatePassword,
    string NewGuestPassword,
    string InternalIpAddress,
    int? RdpExternalPort,
    string StorageRoot,
    bool EnableFiveM,
    int? FiveMExternalPort,
    int FiveMInternalPort);

public sealed record VmHyperVProvisionResult(
    string VmName,
    string VmPath,
    string DiskPath,
    string SwitchName,
    string State,
    long DiskSizeBytes,
    string Message);

public sealed record VmResourceConfiguration(
    string VmName,
    string State,
    int CpuCores,
    int MemoryMb,
    string DiskPath,
    long DiskSizeBytes);

public sealed record VmHyperVResourceResizeResult(
    string VmName,
    string PreviousState,
    string FinalState,
    int OldCpuCores,
    int NewCpuCores,
    int OldMemoryMb,
    int NewMemoryMb,
    long OldDiskSizeBytes,
    long NewDiskSizeBytes,
    string DiskPath,
    bool Restarted,
    string Message);

public sealed record VmResourceResizeResult(
    string VmName,
    string PreviousState,
    string FinalState,
    int OldCpuCores,
    int NewCpuCores,
    int OldMemoryMb,
    int NewMemoryMb,
    int OldDiskGb,
    int NewDiskGb,
    string DiskPath,
    bool Restarted,
    bool GuestPartitionExpanded,
    IReadOnlyList<string> Warnings,
    string Message);

public sealed record VmProvisionResult(
    string VmName,
    long UserId,
    string Username,
    string TemplateName,
    string InternalIpAddress,
    string PublicAddress,
    int RdpExternalPort,
    int? FiveMExternalPort,
    int? FiveMInternalPort,
    string StorageRoot,
    string GuestUsername,
    string GuestPassword,
    string VmPath,
    string DiskPath,
    string State,
    IReadOnlyList<string> Warnings,
    string Message);

public sealed record ReinstallRequest(
    long Id,
    long UserId,
    string Username,
    string VmName,
    long TemplateId,
    string TemplateName,
    string Status,
    string Notes,
    DateTime CreatedAt);

public sealed record ReinstallJob(
    long RequestId,
    string VmName,
    long TemplateId,
    string TemplateName,
    string TemplatePath,
    string Username,
    string Status);

public sealed record VmTemplateCreateResult(
    string TemplatePath,
    bool WasRunning,
    int Generation,
    string Message);

public sealed record VmReinstallPreflight(
    string VmName,
    string TemplateName,
    string CurrentDiskPath,
    long CurrentVirtualDiskBytes,
    long CurrentPhysicalDiskBytes,
    long TemplateVirtualDiskBytes,
    long TemplatePhysicalDiskBytes,
    int CheckpointCount,
    long TargetFreeBytes,
    long TargetTotalBytes,
    long RequiredReserveBytes,
    bool GuestAutomationConfigured,
    bool CanProceed,
    IReadOnlyList<string> Warnings,
    string Summary);

public sealed record VmReinstallResult(
    string VmName,
    string TemplateName,
    string TemplatePath,
    string NewDiskPath,
    string OldDiskRollbackPath,
    long DiskSizeBytes,
    bool WasRunning,
    string PreviousState,
    string FinalState,
    bool GuestConfigured,
    string WindowsUsername,
    IReadOnlyList<string> Warnings,
    string Message);

public sealed record CheckpointRestoreResult(
    string VmName,
    string CheckpointName,
    bool WasRunning,
    string PreviousState,
    string FinalState,
    string Message);

public sealed record VmReinstallRollbackInfo(
    long Id,
    string VmName,
    string TemplateName,
    string RollbackDiskPath,
    string ReinstalledDiskPath,
    long VirtualDiskBytes,
    long PhysicalDiskBytes,
    string Status,
    bool WasRunning,
    string PreviousState,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? RestoredAt,
    DateTime? DeletedAt,
    string LastError)
{
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
}

public sealed record VmReinstallRollbackCleanupResult(
    int Scanned,
    int Deleted,
    int Failed,
    long FreedBytes,
    DateTime CompletedAt);

public sealed record VmReinstallRollbackSecret(
    VmReinstallRollbackInfo Info,
    VmAccessInfo PreviousAccess);

public sealed record VmReinstallRollbackDiskRestoreResult(
    string VmName,
    string RestoredDiskPath,
    string RemovedReinstalledDiskPath,
    string PreviousState,
    string FinalState,
    bool WasRunning,
    string Message);

public sealed record VmReinstallRollbackRestoreResult(
    VmReinstallRollbackInfo Rollback,
    string FinalState,
    IReadOnlyList<string> Warnings,
    string Message);

public sealed record VmBackupDiskSource(
    string Path,
    string ControllerType,
    int ControllerNumber,
    int ControllerLocation,
    long PhysicalSizeBytes,
    long VirtualSizeBytes,
    long EstimatedBackupBytes,
    bool IsDifferencing);

public sealed record VmBackupSourceInfo(
    string VmName,
    string State,
    int CpuCores,
    long MemoryStartupBytes,
    int Generation,
    int CheckpointCount,
    IReadOnlyList<VmBackupDiskSource> Disks);

public sealed record VmBackupDiskRestoreRequest(
    string BackupPath,
    string ControllerType,
    int ControllerNumber,
    int ControllerLocation);

public sealed record VmBackupDiskManifest(
    string RelativePath,
    string OriginalPath,
    string ControllerType,
    int ControllerNumber,
    int ControllerLocation,
    long FileSizeBytes,
    long VirtualSizeBytes);

public sealed record VmBackupManifest(
    int Version,
    string VmName,
    string BackupName,
    string BackupType,
    string OriginalVmState,
    int CpuCores,
    long MemoryStartupBytes,
    int Generation,
    IReadOnlyList<VmBackupDiskManifest> Disks,
    DateTime CreatedAt);

public sealed record VmFullBackupInfo(
    long Id,
    string VmName,
    string Name,
    string BackupType,
    string Status,
    string BackupPath,
    string ManifestPath,
    long TotalBytes,
    int DiskCount,
    bool WasRunning,
    long? CreatedByUserId,
    string CreatedByUsername,
    string LastError,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public sealed record VmBackupStorageRootStatus(
    string Path,
    bool IsDefault,
    bool IsAvailable,
    long FreeBytes,
    long TotalBytes,
    long ReserveBytes,
    string Error);

public sealed record VmBackupStorageStatus(
    string RootPath,
    bool IsAvailable,
    long FreeBytes,
    long TotalBytes,
    long ReserveBytes,
    int MaxBackupsPerVm,
    string Error,
    string SelectionMode,
    IReadOnlyList<VmBackupStorageRootStatus> Roots);

public sealed record VmFullBackupResult(
    VmFullBackupInfo Backup,
    string Message);

public sealed record VmFullBackupRestoreResult(
    string VmName,
    long BackupId,
    string BackupName,
    long SafetyBackupId,
    string SafetyBackupName,
    bool WasRunning,
    string PreviousState,
    string FinalState,
    string Message);

public sealed record AuditLogEntry(
    long Id,
    string Action,
    string ResourceType,
    string? ResourceId,
    string? Details,
    DateTime CreatedAt);

public sealed record AbuseEvent(
    long Id,
    string VmName,
    string DetectionType,
    string Severity,
    double OutboundMbps,
    string ActionTaken,
    DateTime CreatedAt);


public sealed record AbuseScanResult(
    string VmName,
    bool IsBaseline,
    bool Detected,
    double OutboundMbps,
    string Message,
    string ActionTaken,
    DateTime CreatedAt);

public sealed record AbuseMonitorVmStatus(
    string VmName,
    DateTime LastScanAt,
    string Result,
    double OutboundMbps,
    bool Detected,
    DateTime? CooldownUntil);

public sealed record AbuseMonitorRuntimeStatus(
    bool IsRunning,
    DateTime? StartedAt,
    DateTime? LastLoopAt,
    DateTime? LastScanAt,
    string LastMessage,
    IReadOnlyList<AbuseMonitorVmStatus> VmStatuses);



public sealed record HostNodeStatus(
    double CpuPercent,
    long MemoryTotalBytes,
    long MemoryAvailableBytes,
    long MemoryUsedBytes,
    long DiskTotalBytes,
    long DiskFreeBytes,
    int RunningVmCount,
    int StoppedVmCount,
    string HyperVServiceStatus,
    DateTime UpdatedAt);

public sealed record VmMonitoringMeterSample(
    string VmName,
    string State,
    string Status,
    int CpuUsagePercent,
    long MemoryAssignedBytes,
    long MemoryDemandBytes,
    TimeSpan Uptime,
    string HeartbeatStatus,
    bool HeartbeatHealthy,
    int NetworkAdapterCount,
    int ConnectedNetworkAdapterCount,
    bool ResourceMeteringEnabled,
    long NetworkReceivedBytes,
    long NetworkSentBytes,
    long DiskReadBytes,
    long DiskWrittenBytes,
    DateTime SampledAt);

public sealed record HyperVLiveMonitoringBatch(
    HostNodeStatus Host,
    IReadOnlyList<VmMonitoringMeterSample> Vms,
    DateTime SampledAt);

public sealed record VmLiveMonitoringSnapshot(
    string VmName,
    string State,
    string Status,
    int CpuUsagePercent,
    long MemoryAssignedBytes,
    long MemoryDemandBytes,
    TimeSpan Uptime,
    string HeartbeatStatus,
    bool HeartbeatHealthy,
    int NetworkAdapterCount,
    int ConnectedNetworkAdapterCount,
    bool ResourceMeteringEnabled,
    double NetworkReceiveBytesPerSecond,
    double NetworkSendBytesPerSecond,
    double DiskReadBytesPerSecond,
    double DiskWriteBytesPerSecond,
    DateTime SampledAt)
{
    public bool IsRunning => State.Contains("Running", StringComparison.OrdinalIgnoreCase) || State == "2";

    public double MemoryPressurePercent => MemoryAssignedBytes <= 0
        ? 0
        : Math.Clamp(MemoryDemandBytes * 100d / MemoryAssignedBytes, 0, 999);

    public bool HasWarning => IsRunning
        && (!HeartbeatHealthy || ConnectedNetworkAdapterCount < NetworkAdapterCount || MemoryPressurePercent >= 90);
}

public sealed record LiveMonitoringOverview(
    HostNodeStatus? Host,
    IReadOnlyList<VmLiveMonitoringSnapshot> Vms,
    DateTime SampledAt,
    int PollSeconds,
    int StaleAfterSeconds,
    string LastError)
{
    public bool HasSample => SampledAt != DateTime.MinValue;
    public bool IsStale => !HasSample || DateTime.UtcNow - SampledAt > TimeSpan.FromSeconds(StaleAfterSeconds);
}

public sealed class PerformanceHistoryConfig
{
    public bool Enabled { get; set; }
    public int RetentionDays { get; set; }

    public PerformanceHistoryConfig(bool enabled, int retentionDays)
    {
        Enabled = enabled;
        RetentionDays = retentionDays;
    }

    public static PerformanceHistoryConfig Default => new(true, 30);
}

public sealed record HostPerformanceHistoryPoint(
    DateTime SampledAt,
    double CpuPercent,
    double MemoryPercent,
    double DiskFreePercent,
    int RunningVmCount,
    int TotalVmCount);

public sealed record VmPerformanceHistoryPoint(
    DateTime SampledAt,
    double CpuPercent,
    double MemoryPressurePercent,
    double NetworkReceiveBytesPerSecond,
    double NetworkSendBytesPerSecond,
    double DiskReadBytesPerSecond,
    double DiskWriteBytesPerSecond,
    bool IsRunning);

public sealed record PerformanceHistoryRuntimeStatus(
    bool IsRunning,
    DateTime? LastStoredAt,
    DateTime? LastCleanupAt,
    string LastMessage,
    int CollectionSeconds,
    long HostSampleCount,
    long VmSampleCount);

public sealed record PerformanceChartSeries(string Name, string Color, IReadOnlyList<double> Values);

public sealed class MonitoringAlertConfig
{
    public MonitoringAlertConfig(
        bool enabled,
        bool emailEnabled,
        bool recoveryEmails,
        int requiredConsecutiveSamples,
        int reminderMinutes,
        double hostCpuPercent,
        double hostMemoryPercent,
        double hostDiskFreePercent,
        double vmCpuPercent,
        double vmMemoryPressurePercent,
        bool heartbeatAlerts,
        bool adapterAlerts)
    {
        Enabled = enabled;
        EmailEnabled = emailEnabled;
        RecoveryEmails = recoveryEmails;
        RequiredConsecutiveSamples = requiredConsecutiveSamples;
        ReminderMinutes = reminderMinutes;
        HostCpuPercent = hostCpuPercent;
        HostMemoryPercent = hostMemoryPercent;
        HostDiskFreePercent = hostDiskFreePercent;
        VmCpuPercent = vmCpuPercent;
        VmMemoryPressurePercent = vmMemoryPressurePercent;
        HeartbeatAlerts = heartbeatAlerts;
        AdapterAlerts = adapterAlerts;
    }

    public bool Enabled { get; set; }
    public bool EmailEnabled { get; set; }
    public bool RecoveryEmails { get; set; }
    public int RequiredConsecutiveSamples { get; set; }
    public int ReminderMinutes { get; set; }
    public double HostCpuPercent { get; set; }
    public double HostMemoryPercent { get; set; }
    public double HostDiskFreePercent { get; set; }
    public double VmCpuPercent { get; set; }
    public double VmMemoryPressurePercent { get; set; }
    public bool HeartbeatAlerts { get; set; }
    public bool AdapterAlerts { get; set; }

    public static MonitoringAlertConfig Default => new(true, true, true, 3, 30, 90, 90, 10, 90, 100, true, true);
}

public sealed record MonitoringAlert(
    long Id,
    string AlertKey,
    string ScopeType,
    string ScopeName,
    string AlertType,
    string Severity,
    string Title,
    string Message,
    double? CurrentValue,
    double? ThresholdValue,
    string Status,
    int OccurrenceCount,
    DateTime FirstTriggeredAt,
    DateTime LastTriggeredAt,
    DateTime? LastNotifiedAt,
    DateTime? AcknowledgedAt,
    string AcknowledgedBy,
    DateTime? ResolvedAt,
    string ResolutionMessage)
{
    public bool IsOpen => Status is "active" or "acknowledged";
    public bool IsAcknowledged => Status == "acknowledged";
}

public sealed record MonitoringAlertRuntimeStatus(
    bool IsRunning,
    DateTime? StartedAt,
    DateTime? LastEvaluationAt,
    string LastMessage,
    int OpenAlertCount,
    int PendingConditionCount);

public sealed class VmAvailabilityConfig
{
    public VmAvailabilityConfig(
        bool enabled,
        bool clientEmailEnabled,
        bool notifyAdmins,
        int unexpectedDownMinutes,
        int guestUnresponsiveMinutes,
        int extendedDowntimeMinutes,
        bool recoveryNotifications)
    {
        Enabled = enabled;
        ClientEmailEnabled = clientEmailEnabled;
        NotifyAdmins = notifyAdmins;
        UnexpectedDownMinutes = unexpectedDownMinutes;
        GuestUnresponsiveMinutes = guestUnresponsiveMinutes;
        ExtendedDowntimeMinutes = extendedDowntimeMinutes;
        RecoveryNotifications = recoveryNotifications;
    }

    public bool Enabled { get; set; }
    public bool ClientEmailEnabled { get; set; }
    public bool NotifyAdmins { get; set; }
    public int UnexpectedDownMinutes { get; set; }
    public int GuestUnresponsiveMinutes { get; set; }
    public int ExtendedDowntimeMinutes { get; set; }
    public bool RecoveryNotifications { get; set; }

    public static VmAvailabilityConfig Default => new(true, true, true, 1, 10, 60, true);
}

public sealed class VmAvailabilityPreference
{
    public VmAvailabilityPreference(
        long userId,
        string vmName,
        bool enabled,
        bool unexpectedDown,
        bool extendedDowntime,
        bool guestUnresponsive,
        bool recovery)
    {
        UserId = userId;
        VmName = vmName;
        Enabled = enabled;
        NotifyUnexpectedDown = unexpectedDown;
        NotifyExtendedDowntime = extendedDowntime;
        NotifyGuestUnresponsive = guestUnresponsive;
        NotifyRecovery = recovery;
    }

    public long UserId { get; set; }
    public string VmName { get; set; }
    public bool Enabled { get; set; }
    public bool NotifyUnexpectedDown { get; set; }
    public bool NotifyExtendedDowntime { get; set; }
    public bool NotifyGuestUnresponsive { get; set; }
    public bool NotifyRecovery { get; set; }

    public static VmAvailabilityPreference Default(long userId, string vmName) => new(userId, vmName, true, true, true, true, true);
}

public sealed record VmAvailabilityState(
    string VmName,
    string LastState,
    bool LastHeartbeatHealthy,
    bool WasObservedRunning,
    bool DownWasPlanned,
    DateTime LastSampleAt,
    DateTime? DownSince,
    DateTime? UnresponsiveSince,
    DateTime? PlannedUntil,
    string PlannedReason,
    DateTime? UnexpectedNotifiedAt,
    DateTime? ExtendedNotifiedAt,
    DateTime? UnresponsiveNotifiedAt,
    DateTime UpdatedAt)
{
    public bool IsDown => DownSince is not null;
    public bool IsPlannedNow => PlannedUntil is not null && PlannedUntil > DateTime.UtcNow;
}

public sealed record VmAvailabilityEvent(
    long Id,
    string VmName,
    string EventType,
    string Severity,
    string Title,
    string Message,
    DateTime StartedAt,
    DateTime CreatedAt);

public sealed record VmAvailabilityRecipient(
    long UserId,
    string Username,
    string Email,
    VmAvailabilityPreference Preference);

public sealed record VmAvailabilityRuntimeStatus(
    bool IsRunning,
    DateTime? StartedAt,
    DateTime? LastEvaluationAt,
    string LastMessage,
    int TrackedVmCount);

public sealed class VmSelfHealingConfig
{
    public VmSelfHealingConfig(
        bool enabled,
        bool clientEmailEnabled,
        bool notifyAdmins,
        bool unexpectedStopRecovery,
        bool heartbeatRecovery,
        int unexpectedStopDelayMinutes,
        int heartbeatDelayMinutes,
        int confirmationMinutes,
        int cooldownMinutes,
        int maxAttempts,
        int attemptWindowMinutes)
    {
        Enabled = enabled;
        ClientEmailEnabled = clientEmailEnabled;
        NotifyAdmins = notifyAdmins;
        UnexpectedStopRecovery = unexpectedStopRecovery;
        HeartbeatRecovery = heartbeatRecovery;
        UnexpectedStopDelayMinutes = unexpectedStopDelayMinutes;
        HeartbeatDelayMinutes = heartbeatDelayMinutes;
        ConfirmationMinutes = confirmationMinutes;
        CooldownMinutes = cooldownMinutes;
        MaxAttempts = maxAttempts;
        AttemptWindowMinutes = attemptWindowMinutes;
    }

    public bool Enabled { get; set; }
    public bool ClientEmailEnabled { get; set; }
    public bool NotifyAdmins { get; set; }
    public bool UnexpectedStopRecovery { get; set; }
    public bool HeartbeatRecovery { get; set; }
    public int UnexpectedStopDelayMinutes { get; set; }
    public int HeartbeatDelayMinutes { get; set; }
    public int ConfirmationMinutes { get; set; }
    public int CooldownMinutes { get; set; }
    public int MaxAttempts { get; set; }
    public int AttemptWindowMinutes { get; set; }

    public static VmSelfHealingConfig Default => new(false, true, true, true, false, 3, 20, 3, 30, 2, 360);
}

public sealed class VmSelfHealingPreference
{
    public VmSelfHealingPreference(
        string vmName,
        bool enabled,
        bool recoverUnexpectedStop,
        bool recoverUnresponsive,
        DateTime? holdUntil,
        string holdReason,
        DateTime updatedAt)
    {
        VmName = vmName;
        Enabled = enabled;
        RecoverUnexpectedStop = recoverUnexpectedStop;
        RecoverUnresponsive = recoverUnresponsive;
        HoldUntil = holdUntil;
        HoldReason = holdReason;
        UpdatedAt = updatedAt;
    }

    public string VmName { get; set; }
    public bool Enabled { get; set; }
    public bool RecoverUnexpectedStop { get; set; }
    public bool RecoverUnresponsive { get; set; }
    public DateTime? HoldUntil { get; set; }
    public string HoldReason { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool IsHeld => HoldUntil is not null && HoldUntil > DateTime.UtcNow;
    public static VmSelfHealingPreference Default(string vmName) => new(vmName, false, true, false, null, string.Empty, DateTime.UtcNow);
}

public sealed record VmSelfHealingState(
    string VmName,
    string IncidentKey,
    string IncidentType,
    DateTime? IncidentStartedAt,
    int AttemptCount,
    DateTime? WindowStartedAt,
    DateTime? LastAttemptAt,
    DateTime? NextAttemptAt,
    string Status,
    string LastResult,
    DateTime UpdatedAt);

public sealed record VmSelfHealingEvent(
    long Id,
    string VmName,
    string IncidentType,
    string EventType,
    string Status,
    int AttemptNumber,
    string Message,
    DateTime CreatedAt);

public sealed record VmSelfHealingRuntimeStatus(
    bool IsRunning,
    DateTime? StartedAt,
    DateTime? LastEvaluationAt,
    string LastMessage,
    int EligibleVmCount,
    int ActiveIncidentCount);

public sealed record PortMappingConflict(
    string VmName,
    string Protocol,
    int ExternalPort,
    string ConflictVmName,
    string Reason);

public sealed record UserResourceLimits(
    long UserId,
    int MaxVmCount,
    int MaxCpuCores,
    int MaxMemoryMb,
    int MaxDiskGb,
    int MaxPowerActionsPerHour)
{
    public static UserResourceLimits Default(long userId) => new(userId, 0, 0, 0, 0, 20);

    public bool HasVmLimit => MaxVmCount > 0;
    public bool HasCpuLimit => MaxCpuCores > 0;
    public bool HasMemoryLimit => MaxMemoryMb > 0;
    public bool HasDiskLimit => MaxDiskGb > 0;
    public bool HasPowerLimit => MaxPowerActionsPerHour > 0;
}

public sealed record VmPublicStatusSettings(
    string VmName,
    bool IsPublic,
    string DisplayName,
    string PublicNote,
    int? PortCheckPort,
    DateTime UpdatedAt);

public sealed record VmPublicStatus(
    string VmName,
    string DisplayName,
    string State,
    bool IsOnline,
    int CpuUsage,
    long MemoryAssigned,
    TimeSpan Uptime,
    string Address,
    int? Port,
    string Note,
    DateTime UpdatedAt);


public sealed record VmGuestAgentSettings(
    string VmName,
    bool IsEnabled,
    bool HasToken,
    DateTime? LastSeenAt,
    string LastAgentVersion,
    string LastIpAddress,
    DateTime UpdatedAt)
{
    public bool IsOnline => LastSeenAt is not null && LastSeenAt.Value.ToUniversalTime() >= DateTime.UtcNow.AddMinutes(-3);
}

public sealed record GuestAgentCommand(
    long Id,
    string VmName,
    string CommandType,
    string Status,
    string PayloadJson,
    string ResultMessage,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);

public sealed record GuestAgentTokenResult(
    VmGuestAgentSettings Settings,
    string Token);

public sealed record EmergencyPasswordResetQueueResult(
    string Message,
    bool PasswordEmailSent);

public sealed record VmRecoveryAccountSecret(
    string VmName,
    bool IsEnabled,
    string Username,
    string Password,
    DateTime? LastRotatedAt,
    DateTime? LastUsedAt,
    string LastError,
    DateTime UpdatedAt);

public sealed record VmRecoveryAccountStatus(
    string VmName,
    bool IsReady,
    string Username,
    DateTime? LastRotatedAt,
    DateTime? LastUsedAt,
    string LastError);

public sealed record GuestAgentPasswordResetPayload(
    string Username,
    string NewPassword,
    bool ForceLogoffSessions,
    bool RestartRemoteDesktopService,
    bool DisableOtherLocalAdministrators,
    bool DisableRemoteDesktopFirewall);

public sealed record GuestAgentDnsSettingsPayload(
    string InterfaceAlias,
    string PreferredDnsServer,
    string AlternateDnsServer,
    bool ResetToAutomatic);

public sealed record GuestAgentHeartbeatRequest(string AgentVersion);

public sealed record GuestAgentCompleteRequest(bool Success, string Message);

public sealed record GuestAgentCommandResponse(long Id, string CommandType, string PayloadJson);

public sealed record GuestAgentHeartbeatResponse(string VmName, string Status, DateTime ServerTimeUtc);

public sealed class AbuseConfig
{
    public AbuseConfig(
        bool enabled,
        bool autoDisconnect,
        bool autoSuspend,
        double outboundThresholdMbps,
        double sustainedThresholdMbps,
        bool autoScanEnabled = false,
        int scanIntervalSeconds = 30,
        int cooldownMinutes = 5,
        int requiredConsecutiveDetections = 3)
    {
        Enabled = enabled;
        AutoDisconnect = autoDisconnect;
        AutoSuspend = autoSuspend;
        OutboundThresholdMbps = outboundThresholdMbps;
        SustainedThresholdMbps = sustainedThresholdMbps;
        AutoScanEnabled = autoScanEnabled;
        ScanIntervalSeconds = scanIntervalSeconds;
        CooldownMinutes = cooldownMinutes;
        RequiredConsecutiveDetections = requiredConsecutiveDetections;
    }

    public bool Enabled { get; set; }
    public bool AutoDisconnect { get; set; }
    public bool AutoSuspend { get; set; }
    public double OutboundThresholdMbps { get; set; }
    public double SustainedThresholdMbps { get; set; }
    public bool AutoScanEnabled { get; set; }
    public int ScanIntervalSeconds { get; set; }
    public int CooldownMinutes { get; set; }
    public int RequiredConsecutiveDetections { get; set; }
}
