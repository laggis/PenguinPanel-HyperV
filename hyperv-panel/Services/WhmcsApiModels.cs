namespace HyperVPanel.Services;

public sealed class WhmcsCreateVpsRequest
{
    public long ServiceId { get; set; }
    public long ClientId { get; set; }
    public long? PanelUserId { get; set; }
    public string CustomerUsername { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string PanelTemporaryPassword { get; set; } = string.Empty;
    public string VmName { get; set; } = string.Empty;
    public long TemplateId { get; set; }
    public int CpuCores { get; set; } = 2;
    public int MemoryMb { get; set; } = 4096;
    public int DiskGb { get; set; } = 128;
    public int Generation { get; set; } = 2;
    public bool SecureBoot { get; set; } = true;
    public bool StartAfterProvision { get; set; } = true;
    public bool ConfigureWindowsGuest { get; set; } = true;
    public string GuestUsername { get; set; } = "Administrator";
    public string TemplatePassword { get; set; } = string.Empty;
    public string NewGuestPassword { get; set; } = string.Empty;
    public string InternalIpAddress { get; set; } = string.Empty;
    public int? RdpExternalPort { get; set; }
    public string StorageRoot { get; set; } = string.Empty;
    public bool EnableFiveM { get; set; }
    public int? FiveMExternalPort { get; set; }
    // 0 mirrors the collision-safe public FiveM port allocated during provisioning.
    public int FiveMInternalPort { get; set; }
}

public sealed record WhmcsServiceRecord(
    long ServiceId,
    long ClientId,
    long PanelUserId,
    string VmName,
    string Status,
    string LastError,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record WhmcsVpsStatusResponse(
    long ServiceId,
    long ClientId,
    long PanelUserId,
    string VmName,
    string ServiceStatus,
    bool VmExists,
    string VmState,
    string PublicAddress,
    int? RdpPort,
    string InternalIpAddress,
    int? FiveMPort,
    int? FiveMInternalPort,
    string LastError,
    DateTime UpdatedAt);

public sealed record WhmcsCreateVpsResponse(
    long ServiceId,
    long ClientId,
    long PanelUserId,
    string PanelUsername,
    bool PanelAccountCreated,
    string PanelTemporaryPassword,
    string VmName,
    string ServiceStatus,
    string VmState,
    string PublicAddress,
    int RdpPort,
    string InternalIpAddress,
    string StorageRoot,
    int? FiveMPort,
    int? FiveMInternalPort,
    string GuestUsername,
    string GuestPassword,
    bool ExistingService,
    IReadOnlyList<string> Warnings,
    string Message);

public sealed record WhmcsOperationResponse(
    long ServiceId,
    string VmName,
    string ServiceStatus,
    string VmState,
    string Message);

public sealed class WhmcsResizeVpsRequest
{
    public int CpuCores { get; set; } = 2;
    public int MemoryMb { get; set; } = 4096;
    public int DiskGb { get; set; } = 128;
}

public sealed record WhmcsResizeVpsResponse(
    long ServiceId,
    string VmName,
    string ServiceStatus,
    string VmState,
    int OldCpuCores,
    int NewCpuCores,
    int OldMemoryMb,
    int NewMemoryMb,
    int OldDiskGb,
    int NewDiskGb,
    bool Restarted,
    bool GuestPartitionExpanded,
    IReadOnlyList<string> Warnings,
    string Message);

public sealed record WhmcsTemplateInfo(
    long Id,
    string Name,
    string OperatingSystem,
    string Notes);

public sealed record WhmcsStorageRootInfo(
    string Path,
    bool IsDefault,
    bool IsAvailable,
    long FreeBytes,
    long TotalBytes,
    double FreeGb,
    double TotalGb,
    string Error);

public sealed record WhmcsCatalogResponse(
    IReadOnlyList<WhmcsTemplateInfo> Templates,
    IReadOnlyList<string> StorageRoots,
    string DefaultStorageRoot,
    int DefaultGeneration,
    bool DefaultSecureBoot,
    int RdpPortStart,
    int RdpPortEnd,
    int FiveMPortStart,
    int FiveMPortEnd,
    int DefaultFiveMInternalPort,
    IReadOnlyList<WhmcsStorageRootInfo> StorageRootDetails,
    int MinimumFreeSpaceAfterProvisionGb,
    int MinimumFreeSpaceAfterProvisionPercent,
    string StorageSelectionMode);
