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
    DateTime CreationTime);

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

public sealed record VmRdpListenerStatus(
    string VmName,
    int Port,
    bool RemoteDesktopEnabled,
    bool TermServiceRunning,
    bool TcpListening,
    DateTime CheckedAt)
{
    public bool IsReady => RemoteDesktopEnabled && TermServiceRunning && TcpListening;
}

public sealed record VmTemplate(
    long Id,
    string Name,
    string OperatingSystem,
    string IsoPath,
    string Notes,
    bool IsEnabled,
    DateTime CreatedAt);

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
    string Message);

public sealed record VmReinstallResult(
    string VmName,
    string TemplateName,
    string TemplatePath,
    string NewDiskPath,
    bool WasRunning,
    string PreviousState,
    string FinalState,
    string Message);

public sealed record CheckpointRestoreResult(
    string VmName,
    string CheckpointName,
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
