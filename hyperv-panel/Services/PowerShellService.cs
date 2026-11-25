using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HyperVPanel.Services;

public sealed class PowerShellService(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<VmInfo>> GetVmsAsync()
    {
        var output = await RunJsonAsync("""
            Get-VM | Select-Object Name, State, CPUUsage, MemoryAssigned, Uptime, Status | ConvertTo-Json -Depth 4
            """);
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var items = output.TrimStart().StartsWith("[", StringComparison.Ordinal)
            ? JsonSerializer.Deserialize<List<VmJson>>(output, JsonOptions) ?? []
            : [JsonSerializer.Deserialize<VmJson>(output, JsonOptions)!];

        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new VmInfo(
                item.Name!,
                item.State?.ToString() ?? "Unknown",
                item.CPUUsage,
                item.MemoryAssigned,
                ParseTimeSpan(item.Uptime),
                item.Status?.ToString() ?? ""))
            .ToList();
    }

    public async Task<VmInfo?> GetVmAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var script =
            "$vm = Get-VM -Name " + Quote(name) + " -ErrorAction SilentlyContinue; " +
            "if ($null -eq $vm) { return }; " +
            "$vm | Select-Object Name, State, CPUUsage, MemoryAssigned, Uptime, Status | ConvertTo-Json -Compress -Depth 4";

        var output = await RunJsonAsync(script);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var item = JsonSerializer.Deserialize<VmJson>(output, JsonOptions);
        if (item is null || string.IsNullOrWhiteSpace(item.Name))
        {
            return null;
        }

        return new VmInfo(
            item.Name,
            item.State?.ToString() ?? "Unknown",
            item.CPUUsage,
            item.MemoryAssigned,
            ParseTimeSpan(item.Uptime),
            item.Status?.ToString() ?? string.Empty);
    }

    public async Task<HostNodeStatus> GetHostNodeStatusAsync()
    {
        var script = """
            $ErrorActionPreference = 'Stop';
            $cpu = 0;
            try {
                $processor = Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation -Filter "Name='_Total'" -ErrorAction Stop | Select-Object -First 1;
                if ($null -ne $processor -and $null -ne $processor.PercentProcessorUtility) {
                    $cpu = [double]$processor.PercentProcessorUtility;
                } else { throw 'Processor utility counter is unavailable.' }
            } catch {
                try {
                    $processor = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -ErrorAction Stop | Select-Object -First 1;
                    $cpu = [double]$processor.PercentProcessorTime;
                } catch {
                    try { $cpu = [double]((Get-CimInstance Win32_Processor -ErrorAction Stop | Measure-Object -Property LoadPercentage -Average).Average) } catch { $cpu = 0 }
                }
            }
            $cpu = [math]::Round([math]::Min(100, [math]::Max(0, $cpu)), 1);
            $os = Get-CimInstance Win32_OperatingSystem;
            $memoryTotal = [int64]($os.TotalVisibleMemorySize * 1024);
            $memoryFree = [int64]($os.FreePhysicalMemory * 1024);
            $memoryUsed = [int64]($memoryTotal - $memoryFree);
            $systemDrive = $env:SystemDrive.TrimEnd('\');
            $disk = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$systemDrive'";
            $vms = @(Get-VM -ErrorAction SilentlyContinue);
            $service = Get-Service vmms -ErrorAction SilentlyContinue;
            [pscustomobject]@{
                CpuPercent = [double]$cpu;
                MemoryTotalBytes = $memoryTotal;
                MemoryAvailableBytes = $memoryFree;
                MemoryUsedBytes = $memoryUsed;
                DiskTotalBytes = if ($disk) { [int64]$disk.Size } else { [int64]0 };
                DiskFreeBytes = if ($disk) { [int64]$disk.FreeSpace } else { [int64]0 };
                RunningVmCount = @($vms | Where-Object { [string]$_.State -eq 'Running' }).Count;
                StoppedVmCount = @($vms | Where-Object { [string]$_.State -in @('Off','Saved','Paused','Stopped') }).Count;
                HyperVServiceStatus = if ($service) { [string]$service.Status } else { 'Unknown' };
                UpdatedAt = (Get-Date).ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
            } | ConvertTo-Json -Compress -Depth 4
            """;

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<HostNodeStatusJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Host status returned no result.");

        return new HostNodeStatus(
            item.CpuPercent,
            item.MemoryTotalBytes,
            item.MemoryAvailableBytes,
            item.MemoryUsedBytes,
            item.DiskTotalBytes,
            item.DiskFreeBytes,
            item.RunningVmCount,
            item.StoppedVmCount,
            item.HyperVServiceStatus ?? "Unknown",
            ParseDateTime(item.UpdatedAt));
    }

    public async Task<HyperVLiveMonitoringBatch> GetLiveMonitoringBatchAsync(bool autoEnableResourceMetering)
    {
        var enableMetering = autoEnableResourceMetering ? "$true" : "$false";
        var script = $$"""
            $ErrorActionPreference = 'Stop';
            $autoEnableMetering = {{enableMetering}};
            function Convert-ToInt64($value) {
                if ($null -eq $value) { return [int64]0 }
                try { return [int64][double]$value } catch { return [int64]0 }
            }
            function Get-TrafficMegabytes($row) {
                if ($null -eq $row) { return [int64]0 }
                $prop = $row.PSObject.Properties['TotalTraffic'];
                if ($null -ne $prop) { return Convert-ToInt64 $prop.Value }
                $prop = $row.PSObject.Properties['TotalTraffic(M)'];
                if ($null -ne $prop) { return Convert-ToInt64 $prop.Value }
                return [int64]0
            }

            $sampledAt = (Get-Date).ToUniversalTime();
            $cpu = 0;
            try {
                $processor = Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation -Filter "Name='_Total'" -ErrorAction Stop | Select-Object -First 1;
                if ($null -ne $processor -and $null -ne $processor.PercentProcessorUtility) {
                    $cpu = [double]$processor.PercentProcessorUtility;
                } else { throw 'Processor utility counter is unavailable.' }
            } catch {
                try {
                    $processor = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -ErrorAction Stop | Select-Object -First 1;
                    $cpu = [double]$processor.PercentProcessorTime;
                } catch {
                    try { $cpu = [double]((Get-CimInstance Win32_Processor -ErrorAction Stop | Measure-Object -Property LoadPercentage -Average).Average) } catch { $cpu = 0 }
                }
            }
            $cpu = [math]::Round([math]::Min(100, [math]::Max(0, $cpu)), 1);
            $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop;
            $memoryTotal = [int64]($os.TotalVisibleMemorySize * 1024);
            $memoryFree = [int64]($os.FreePhysicalMemory * 1024);
            $systemDrive = $env:SystemDrive.TrimEnd('\');
            $disk = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$systemDrive'" -ErrorAction SilentlyContinue;
            $service = Get-Service vmms -ErrorAction SilentlyContinue;
            $vms = @(Get-VM -ErrorAction SilentlyContinue);

            $vmSamples = @();
            foreach ($vm in $vms) {
                $meteringEnabled = [bool]$vm.ResourceMeteringEnabled;
                if ($autoEnableMetering -and -not $meteringEnabled) {
                    try {
                        Enable-VMResourceMetering -VMName $vm.Name -ErrorAction Stop;
                        $meteringEnabled = $true;
                    } catch { }
                }

                $meter = $null;
                if ($meteringEnabled) {
                    try { $meter = Measure-VM -VMName $vm.Name -ErrorAction Stop } catch { $meter = $null }
                }

                $bytesIn = [int64]0;
                $bytesOut = [int64]0;
                if ($null -ne $meter) {
                    foreach ($row in @($meter.NetworkMeteredTrafficReport)) {
                        $trafficBytes = [int64]((Get-TrafficMegabytes $row) * 1048576);
                        $direction = [string]$row.Direction;
                        if ($direction -eq 'Inbound') { $bytesIn += $trafficBytes }
                        elseif ($direction -eq 'Outbound') { $bytesOut += $trafficBytes }
                    }
                    if (($bytesIn -eq 0) -and ($bytesOut -eq 0)) {
                        $bytesIn = Convert-ToInt64 $meter.TotalIncomingTraffic;
                        $bytesOut = Convert-ToInt64 $meter.TotalOutgoingTraffic;
                    }
                }

                $diskReadBytes = [int64]0;
                $diskWrittenBytes = [int64]0;
                if ($null -ne $meter) {
                    $diskReadBytes = [int64]((Convert-ToInt64 $meter.AggregatedDiskDataRead) * 1048576);
                    $diskWrittenBytes = [int64]((Convert-ToInt64 $meter.AggregatedDiskDataWritten) * 1048576);
                }

                $heartbeat = @(Get-VMIntegrationService -VMName $vm.Name -ErrorAction SilentlyContinue |
                    Where-Object { [string]$_.Id -eq '84EAAE65-2F2E-45F5-9BB5-0E857DC8EB47' -or [string]$_.Name -eq 'Heartbeat' } |
                    Select-Object -First 1)[0];
                $heartbeatText = if ([string]$vm.State -ne 'Running') { 'VM not running' }
                    elseif ($null -eq $heartbeat) { 'Unavailable' }
                    elseif (-not [bool]$heartbeat.Enabled) { 'Disabled' }
                    elseif (-not [string]::IsNullOrWhiteSpace([string]$heartbeat.PrimaryStatusDescription)) { [string]$heartbeat.PrimaryStatusDescription }
                    else { [string]$heartbeat.SecondaryStatusDescription };
                $heartbeatHealthy = [string]$vm.State -eq 'Running' -and $null -ne $heartbeat -and [bool]$heartbeat.Enabled -and
                    ([string]$heartbeat.PrimaryStatusDescription -match '^(OK|Healthy)$');
                $adapters = @(Get-VMNetworkAdapter -VMName $vm.Name -ErrorAction SilentlyContinue);

                $vmSamples += [pscustomobject]@{
                    VmName = [string]$vm.Name;
                    State = [string]$vm.State;
                    Status = [string]$vm.Status;
                    CpuUsagePercent = [int]$vm.CPUUsage;
                    MemoryAssignedBytes = [int64]$vm.MemoryAssigned;
                    MemoryDemandBytes = [int64]$vm.MemoryDemand;
                    UptimeSeconds = [double]$vm.Uptime.TotalSeconds;
                    HeartbeatStatus = $heartbeatText;
                    HeartbeatHealthy = [bool]$heartbeatHealthy;
                    NetworkAdapterCount = $adapters.Count;
                    ConnectedNetworkAdapterCount = @($adapters | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.SwitchName) }).Count;
                    ResourceMeteringEnabled = [bool]$meteringEnabled;
                    NetworkReceivedBytes = $bytesIn;
                    NetworkSentBytes = $bytesOut;
                    DiskReadBytes = $diskReadBytes;
                    DiskWrittenBytes = $diskWrittenBytes;
                    SampledAt = $sampledAt.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture);
                };
            }

            [pscustomobject]@{
                Host = [pscustomobject]@{
                    CpuPercent = [double]$cpu;
                    MemoryTotalBytes = $memoryTotal;
                    MemoryAvailableBytes = $memoryFree;
                    MemoryUsedBytes = [int64]($memoryTotal - $memoryFree);
                    DiskTotalBytes = if ($disk) { [int64]$disk.Size } else { [int64]0 };
                    DiskFreeBytes = if ($disk) { [int64]$disk.FreeSpace } else { [int64]0 };
                    RunningVmCount = @($vms | Where-Object { [string]$_.State -eq 'Running' }).Count;
                    StoppedVmCount = @($vms | Where-Object { [string]$_.State -in @('Off','Saved','Paused','Stopped') }).Count;
                    HyperVServiceStatus = if ($service) { [string]$service.Status } else { 'Unknown' };
                    UpdatedAt = $sampledAt.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture);
                };
                Vms = @($vmSamples);
                SampledAt = $sampledAt.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture);
            } | ConvertTo-Json -Compress -Depth 7;
            """;

        var output = await RunJsonAsync(script);
        var batch = JsonSerializer.Deserialize<LiveMonitoringBatchJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Hyper-V live monitoring returned no result.");
        var host = batch.Host ?? throw new InvalidOperationException("Hyper-V live monitoring returned no host result.");
        var sampledAt = ParseDateTime(batch.SampledAt);

        return new HyperVLiveMonitoringBatch(
            new HostNodeStatus(
                host.CpuPercent,
                host.MemoryTotalBytes,
                host.MemoryAvailableBytes,
                host.MemoryUsedBytes,
                host.DiskTotalBytes,
                host.DiskFreeBytes,
                host.RunningVmCount,
                host.StoppedVmCount,
                host.HyperVServiceStatus ?? "Unknown",
                ParseDateTime(host.UpdatedAt)),
            (batch.Vms ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.VmName))
                .Select(item => new VmMonitoringMeterSample(
                    item.VmName!,
                    item.State ?? "Unknown",
                    item.Status ?? string.Empty,
                    item.CpuUsagePercent,
                    item.MemoryAssignedBytes,
                    item.MemoryDemandBytes,
                    TimeSpan.FromSeconds(Math.Max(0, item.UptimeSeconds)),
                    item.HeartbeatStatus ?? "Unavailable",
                    item.HeartbeatHealthy,
                    item.NetworkAdapterCount,
                    item.ConnectedNetworkAdapterCount,
                    item.ResourceMeteringEnabled,
                    item.NetworkReceivedBytes,
                    item.NetworkSentBytes,
                    item.DiskReadBytes,
                    item.DiskWrittenBytes,
                    ParseDateTime(item.SampledAt)))
                .ToList(),
            sampledAt);
    }

    public Task StartVmAsync(string name) => RunAsync($"Start-VM -Name {Quote(name)}");

    public async Task<string> GetVmStateAsync(string name)
    {
        var output = await RunAsync($"(Get-VM -Name {Quote(name)} -ErrorAction Stop).State.ToString()");
        return string.IsNullOrWhiteSpace(output) ? "Unknown" : output.Trim();
    }

    public Task StopVmForCheckpointRestoreAsync(string name)
    {
        var script =
            "$name = " + Quote(name) + "; " +
            "$vm = Get-VM -Name $name -ErrorAction Stop; " +
            "$state = [string]$vm.State; " +
            "if ($state -in @('Running','Starting','Resuming')) { " +
            "Stop-VM -Name $name -Force -Confirm:$false -ErrorAction Stop; " +
            "} " +
            "$deadline = (Get-Date).AddSeconds(90); " +
            "do { " +
            "$current = [string](Get-VM -Name $name -ErrorAction Stop).State; " +
            "if ($current -notin @('Running','Stopping','Starting','Resuming','Pausing')) { break } " +
            "Start-Sleep -Seconds 2; " +
            "} while ((Get-Date) -lt $deadline); " +
            "$final = [string](Get-VM -Name $name -ErrorAction Stop).State; " +
            "if ($final -in @('Running','Stopping','Starting','Resuming','Pausing')) { throw \"The VM did not stop in time. Current state: $final\" }";

        return RunAsync(script);
    }

    public Task StopVmAsync(string name) => RunAsync($"Stop-VM -Name {Quote(name)} -Force");

    public Task RestartVmAsync(string name) => RunAsync($"Restart-VM -Name {Quote(name)} -Force");

    public Task SuspendVmAsync(string name) => RunAsync($"Suspend-VM -Name {Quote(name)}");

    public Task ResumeVmAsync(string name) => RunAsync($"Resume-VM -Name {Quote(name)}");

    public Task DisconnectVmNetworkAsync(string name) => RunAsync($"Get-VMNetworkAdapter -VMName {Quote(name)} | Disconnect-VMNetworkAdapter");

    public async Task<VmResourceConfiguration> GetVmResourceConfigurationAsync(string vmName)
    {
        if (string.IsNullOrWhiteSpace(vmName))
        {
            throw new InvalidOperationException("VM name is required.");
        }

        var script = $$"""
            $ErrorActionPreference = 'Stop';
            $vmName = {{Quote(vmName)}};
            $vm = Get-VM -Name $vmName -ErrorAction Stop;
            $processor = Get-VMProcessor -VMName $vmName -ErrorAction Stop;
            $memory = Get-VMMemory -VMName $vmName -ErrorAction Stop;
            $diskDrive = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction Stop | Select-Object -First 1)[0];
            if ($null -eq $diskDrive -or [string]::IsNullOrWhiteSpace([string]$diskDrive.Path)) { throw "No virtual hard disk was found on VM '$vmName'." }
            $vhd = Get-VHD -Path $diskDrive.Path -ErrorAction Stop;
            [pscustomobject]@{
                VmName = [string]$vm.Name;
                State = [string]$vm.State;
                CpuCores = [int]$processor.Count;
                MemoryMb = [int][math]::Round([double]$memory.Startup / 1MB);
                DiskPath = [string]$diskDrive.Path;
                DiskSizeBytes = [int64]$vhd.Size;
            } | ConvertTo-Json -Compress -Depth 4;
            """;

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<VmResourceJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Hyper-V returned no resource information for the VM.");

        return new VmResourceConfiguration(
            item.VmName ?? vmName,
            item.State ?? "Unknown",
            Math.Max(1, item.CpuCores),
            Math.Max(0, item.MemoryMb),
            item.DiskPath ?? string.Empty,
            Math.Max(0, item.DiskSizeBytes));
    }

    public async Task<VmHyperVResourceResizeResult> ResizeVmResourcesAsync(string vmName, int cpuCores, int memoryMb, int diskGb)
    {
        if (string.IsNullOrWhiteSpace(vmName))
        {
            throw new InvalidOperationException("VM name is required.");
        }
        if (cpuCores < 1)
        {
            throw new InvalidOperationException("CPU cores must be at least 1.");
        }
        if (memoryMb < 512)
        {
            throw new InvalidOperationException("Memory must be at least 512 MB.");
        }
        if (diskGb < 1)
        {
            throw new InvalidOperationException("Disk size must be at least 1 GB.");
        }

        var script = $$"""
            $ErrorActionPreference = 'Stop';
            $ProgressPreference = 'SilentlyContinue';
            $vmName = {{Quote(vmName)}};
            $newCpu = {{Math.Max(1, cpuCores).ToString(CultureInfo.InvariantCulture)}};
            $newMemoryMb = {{Math.Max(512, memoryMb).ToString(CultureInfo.InvariantCulture)}};
            $newDiskGb = {{Math.Max(1, diskGb).ToString(CultureInfo.InvariantCulture)}};
            $newMemoryBytes = [int64]$newMemoryMb * 1MB;
            $newDiskBytes = [int64]$newDiskGb * 1GB;

            $vm = Get-VM -Name $vmName -ErrorAction Stop;
            $processor = Get-VMProcessor -VMName $vmName -ErrorAction Stop;
            $memory = Get-VMMemory -VMName $vmName -ErrorAction Stop;
            $diskDrive = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction Stop | Select-Object -First 1)[0];
            if ($null -eq $diskDrive -or [string]::IsNullOrWhiteSpace([string]$diskDrive.Path)) { throw "No virtual hard disk was found on VM '$vmName'." }
            $diskPath = [string]$diskDrive.Path;
            $vhd = Get-VHD -Path $diskPath -ErrorAction Stop;

            $previousState = [string]$vm.State;
            $wasRunning = $previousState -eq 'Running';
            $oldCpu = [int]$processor.Count;
            $oldMemoryMb = [int][math]::Round([double]$memory.Startup / 1MB);
            $oldDiskBytes = [int64]$vhd.Size;

            if ($newDiskBytes -lt $oldDiskBytes) {
                $oldDiskGb = [math]::Ceiling([double]$oldDiskBytes / 1GB);
                throw "Disk downgrade is not supported. Current virtual disk is $oldDiskGb GB and the requested size is $newDiskGb GB.";
            }

            $needsChange = ($oldCpu -ne $newCpu) -or ($oldMemoryMb -ne $newMemoryMb) -or ($newDiskBytes -gt $oldDiskBytes);
            $restarted = $false;

            if ($needsChange -and $previousState -ne 'Off') {
                if ($previousState -eq 'Running') {
                    Stop-VM -Name $vmName -Force -Confirm:$false -ErrorAction Stop;
                } else {
                    Stop-VM -Name $vmName -TurnOff -Confirm:$false -ErrorAction Stop;
                }

                $deadline = (Get-Date).AddMinutes(6);
                do {
                    $currentState = [string](Get-VM -Name $vmName -ErrorAction Stop).State;
                    if ($currentState -eq 'Off') { break }
                    Start-Sleep -Seconds 2;
                } while ((Get-Date) -lt $deadline);

                if ([string](Get-VM -Name $vmName -ErrorAction Stop).State -ne 'Off') {
                    throw "VM '$vmName' did not reach the Off state in time for the resource change.";
                }
                $restarted = $wasRunning;
            }

            if ($oldCpu -ne $newCpu) {
                Set-VMProcessor -VMName $vmName -Count $newCpu -ErrorAction Stop;
            }
            if ($oldMemoryMb -ne $newMemoryMb) {
                Set-VMMemory -VMName $vmName -DynamicMemoryEnabled $false -StartupBytes $newMemoryBytes -ErrorAction Stop;
            }
            if ($newDiskBytes -gt $oldDiskBytes) {
                if ([System.IO.Path]::GetExtension($diskPath) -ieq '.avhdx') {
                    throw "The VM is currently using a checkpoint differencing disk (.avhdx). Merge/remove checkpoints before increasing disk size.";
                }
                Resize-VHD -Path $diskPath -SizeBytes $newDiskBytes -ErrorAction Stop;
            }

            if ($wasRunning -and $needsChange) {
                Start-VM -Name $vmName -ErrorAction Stop | Out-Null;
            }

            $finalVm = Get-VM -Name $vmName -ErrorAction Stop;
            $finalDisk = Get-VHD -Path $diskPath -ErrorAction Stop;
            [pscustomobject]@{
                VmName = $vmName;
                PreviousState = $previousState;
                FinalState = [string]$finalVm.State;
                OldCpuCores = $oldCpu;
                NewCpuCores = $newCpu;
                OldMemoryMb = $oldMemoryMb;
                NewMemoryMb = $newMemoryMb;
                OldDiskSizeBytes = $oldDiskBytes;
                NewDiskSizeBytes = [int64]$finalDisk.Size;
                DiskPath = $diskPath;
                Restarted = [bool]$restarted;
                Message = if ($needsChange) { "Hyper-V resources updated successfully." } else { "The requested resources already match the VM." };
            } | ConvertTo-Json -Compress -Depth 4;
            """;

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<VmResourceJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Hyper-V resource change returned no result.");

        return new VmHyperVResourceResizeResult(
            item.VmName ?? vmName,
            item.PreviousState ?? "Unknown",
            item.FinalState ?? "Unknown",
            item.OldCpuCores,
            item.NewCpuCores,
            item.OldMemoryMb,
            item.NewMemoryMb,
            item.OldDiskSizeBytes,
            item.NewDiskSizeBytes,
            item.DiskPath ?? string.Empty,
            item.Restarted,
            item.Message ?? "Hyper-V resources updated.");
    }

    public async Task<bool> ExpandWindowsGuestSystemPartitionAsync(string vmName, string username, string password)
    {
        ValidateGuestCredentials(username, password);

        var script = $$"""
            $ErrorActionPreference = 'Stop';
            $ProgressPreference = 'SilentlyContinue';
            $vmName = {{Quote(vmName)}};
            $username = {{Quote(username)}};
            $plainPassword = {{Quote(password)}};
            $securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force;
            $credential = [pscredential]::new($username, $securePassword);
            $guestScript = {
                $ErrorActionPreference = 'Stop';
                $partition = Get-Partition -DriveLetter C -ErrorAction Stop;
                $supported = Get-PartitionSupportedSize -DriveLetter C -ErrorAction Stop;
                $changed = $false;
                if ([int64]$supported.SizeMax -gt ([int64]$partition.Size + 16MB)) {
                    Resize-Partition -DriveLetter C -Size $supported.SizeMax -ErrorAction Stop;
                    $changed = $true;
                }
                $final = Get-Partition -DriveLetter C -ErrorAction Stop;
                [pscustomobject]@{
                    Changed = [bool]$changed;
                    SizeBytes = [int64]$final.Size;
                }
            };
            $result = Invoke-Command -VMName $vmName -Credential $credential -ScriptBlock $guestScript -ErrorAction Stop;
            $result | ConvertTo-Json -Compress -Depth 4;
            """;

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<GuestPartitionResizeJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Windows partition resize returned no result.");
        return item.Changed;
    }

    public async Task<VmHyperVProvisionResult> ProvisionVmFromTemplateAsync(
        string vmName,
        string templateDiskPath,
        string storageRoot,
        string ownerFolderName,
        string switchName,
        int cpuCores,
        int memoryMb,
        int diskGb,
        int generation,
        bool secureBoot,
        bool startAfter)
    {
        var script =
            "$ErrorActionPreference = 'Stop'; " +
            "$vmName = " + Quote(vmName) + "; " +
            "$templatePath = " + Quote(templateDiskPath) + "; " +
            "$storageRoot = " + Quote(storageRoot) + "; " +
            "$ownerFolder = " + Quote(ownerFolderName) + "; " +
            "$switchName = " + Quote(switchName) + "; " +
            "$cpu = " + Math.Max(1, cpuCores).ToString(CultureInfo.InvariantCulture) + "; " +
            "$memoryMb = " + Math.Max(512, memoryMb).ToString(CultureInfo.InvariantCulture) + "; " +
            "$diskGb = " + Math.Max(1, diskGb).ToString(CultureInfo.InvariantCulture) + "; " +
            "$generation = " + (generation == 1 ? "1" : "2") + "; " +
            "$secureBoot = " + (secureBoot ? "$true" : "$false") + "; " +
            "$startAfter = " + (startAfter ? "$true" : "$false") + "; " +
            "$vmCreated = $false; " +
            "$stage = 'initial validation'; " +
            "$vmFolder = Join-Path (Join-Path $storageRoot $ownerFolder) $vmName; " +
            "$diskFolder = Join-Path $vmFolder 'Virtual Hard Disks'; " +
            "$extension = [System.IO.Path]::GetExtension($templatePath).ToLowerInvariant(); " +
            "if ($extension -notin @('.vhd','.vhdx')) { throw 'Provisioning templates must be VHD or VHDX files.' } " +
            "if (-not (Test-Path -LiteralPath $templatePath)) { throw \"Template disk was not found: $templatePath\" } " +
            "if (Get-VM -Name $vmName -ErrorAction SilentlyContinue) { throw \"A Hyper-V VM named '$vmName' already exists.\" } " +
            "if (Test-Path -LiteralPath $vmFolder) { throw \"The destination folder already exists: $vmFolder\" } " +
            "if ([string]::IsNullOrWhiteSpace($switchName)) { throw 'Provisioning switch name is empty.' } " +
            "$switch = Get-VMSwitch -Name $switchName -ErrorAction SilentlyContinue; " +
            "if ($null -eq $switch) { $available = (@(Get-VMSwitch | Select-Object -ExpandProperty Name) -join ', '); throw (\"Hyper-V switch '$switchName' was not found. Available switches: $available\") } " +
            "$stage = 'reading template VHD/VHDX'; " +
            "$sourceVhd = Get-VHD -Path $templatePath -ErrorAction Stop; " +
            "$requestedDiskBytes = [int64]$diskGb * 1GB; " +
            "if ($requestedDiskBytes -lt [int64]$sourceVhd.Size) { $sourceGb = [math]::Ceiling([double]$sourceVhd.Size / 1GB); throw \"Requested disk size $diskGb GB is smaller than the template virtual size ($sourceGb GB). Increase the requested disk size.\" } " +
            "$diskPath = Join-Path $diskFolder ($vmName + $extension); " +
            "try { " +
            "$stage = 'creating VPS storage folder'; " +
            "New-Item -ItemType Directory -Path $diskFolder -Force -ErrorAction Stop | Out-Null; " +
            "$stage = 'copying template disk'; " +
            "Copy-Item -LiteralPath $templatePath -Destination $diskPath -Force -ErrorAction Stop; " +
            "$stage = 'reading copied virtual disk'; " +
            "$copiedVhd = Get-VHD -Path $diskPath -ErrorAction Stop; " +
            "if ($requestedDiskBytes -gt [int64]$copiedVhd.Size) { $stage = 'resizing virtual disk'; Resize-VHD -Path $diskPath -SizeBytes $requestedDiskBytes -ErrorAction Stop; } " +
            "$memoryBytes = [int64]$memoryMb * 1MB; " +
            "$stage = 'creating Hyper-V VM'; " +
            "New-VM -Name $vmName -Generation $generation -MemoryStartupBytes $memoryBytes -VHDPath $diskPath -Path $vmFolder -SwitchName $switchName -ErrorAction Stop | Out-Null; " +
            "$vmCreated = $true; " +
            "$stage = 'configuring CPU and memory'; " +
            "Set-VMProcessor -VMName $vmName -Count $cpu -ErrorAction Stop; " +
            "Set-VMMemory -VMName $vmName -DynamicMemoryEnabled $false -StartupBytes $memoryBytes -ErrorAction Stop; " +
            "$stage = 'configuring VM defaults'; " +
            "Set-VM -Name $vmName -AutomaticCheckpointsEnabled $false -AutomaticStartAction Nothing -AutomaticStopAction ShutDown -ErrorAction Stop; " +
            "$stage = 'configuring Secure Boot'; " +
            "if ($generation -eq 2) { if ($secureBoot) { Set-VMFirmware -VMName $vmName -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows -ErrorAction Stop } else { Set-VMFirmware -VMName $vmName -EnableSecureBoot Off -ErrorAction Stop } } " +
            "if ($startAfter) { $stage = 'starting Hyper-V VM'; Start-VM -Name $vmName -ErrorAction Stop | Out-Null; } " +
            "$stage = 'reading final VM state'; " +
            "$vm = Get-VM -Name $vmName -ErrorAction Stop; " +
            "$finalVhd = Get-VHD -Path $diskPath -ErrorAction Stop; " +
            "[pscustomobject]@{ VmName=$vmName; VmPath=$vmFolder; DiskPath=$diskPath; SwitchName=$switchName; State=[string]$vm.State; DiskSizeBytes=[int64]$finalVhd.Size; Message=\"VM '$vmName' created from template and connected to '$switchName'.\" } | ConvertTo-Json -Compress -Depth 4; " +
            "} catch { " +
            "if ($vmCreated) { try { Stop-VM -Name $vmName -TurnOff -Force -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } catch {}; try { Remove-VM -Name $vmName -Force -ErrorAction SilentlyContinue } catch {} } " +
            "if (Test-Path -LiteralPath $vmFolder) { try { Remove-Item -LiteralPath $vmFolder -Recurse -Force -ErrorAction SilentlyContinue } catch {} } " +
            "$failureMessage = [string]$_.Exception.Message; if ([string]::IsNullOrWhiteSpace($failureMessage)) { $failureMessage = [string]$_ }; throw (\"Provisioning failed during ${stage}: $failureMessage\"); " +
            "}";

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<VmProvisionJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("VM provisioning returned no result.");

        return new VmHyperVProvisionResult(
            result.VmName ?? vmName,
            result.VmPath ?? string.Empty,
            result.DiskPath ?? string.Empty,
            result.SwitchName ?? switchName,
            result.State ?? "Unknown",
            result.DiskSizeBytes,
            result.Message ?? "VM created.");
    }

    public async Task<IReadOnlySet<string>> GetUsedProvisioningIpv4AddressesAsync()
    {
        var output = await RunJsonAsync("""
            $values = @();
            $values += @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty IPAddress);
            $values += @(Get-VMNetworkAdapter -All -ErrorAction SilentlyContinue | ForEach-Object { @($_.IPAddresses) });
            @($values | Where-Object { $_ -match '^\d{1,3}(\.\d{1,3}){3}$' } | Sort-Object -Unique) | ConvertTo-Json -Compress
            """);

        if (string.IsNullOrWhiteSpace(output))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            if (output.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                return (JsonSerializer.Deserialize<List<string>>(output, JsonOptions) ?? [])
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            var single = JsonSerializer.Deserialize<string>(output, JsonOptions);
            return string.IsNullOrWhiteSpace(single)
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>([single], StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<IReadOnlySet<int>> GetUsedNatExternalPortsAsync(string natName)
    {
        var normalized = NormalizeNatName(natName);
        var script =
            "$natName = " + Quote(normalized) + "; " +
            "$items = if ([string]::IsNullOrWhiteSpace($natName)) { @(Get-NetNatStaticMapping -ErrorAction SilentlyContinue) } else { @(Get-NetNatStaticMapping -NatName $natName -ErrorAction SilentlyContinue) }; " +
            "@($items | Where-Object { $_.ExternalPort -gt 0 } | Select-Object -ExpandProperty ExternalPort -Unique) | ConvertTo-Json -Compress";

        var output = await RunJsonAsync(script);
        if (string.IsNullOrWhiteSpace(output))
        {
            return new HashSet<int>();
        }

        try
        {
            if (output.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                return (JsonSerializer.Deserialize<List<int>>(output, JsonOptions) ?? []).ToHashSet();
            }

            if (int.TryParse(output.Trim(), out var directPort))
            {
                return new HashSet<int>([directPort]);
            }

            var single = JsonSerializer.Deserialize<int>(output, JsonOptions);
            return new HashSet<int>([single]);
        }
        catch
        {
            return new HashSet<int>();
        }
    }

    public async Task<IReadOnlySet<int>> GetUsedHostListenerPortsAsync()
    {
        // TCAdmin and other services may bind ports directly on the Hyper-V host rather
        // than through WinNAT. Treat any active TCP listener or UDP endpoint as used so
        // PenguinPanel never selects it for an automatically provisioned VPS.
        var script =
            "$tcp = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -gt 0 } | Select-Object -ExpandProperty LocalPort); " +
            "$udp = @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -gt 0 } | Select-Object -ExpandProperty LocalPort); " +
            "@($tcp + $udp | Sort-Object -Unique) | ConvertTo-Json -Compress";

        var output = await RunJsonAsync(script);
        if (string.IsNullOrWhiteSpace(output))
        {
            return new HashSet<int>();
        }

        try
        {
            if (output.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                return (JsonSerializer.Deserialize<List<int>>(output, JsonOptions) ?? []).ToHashSet();
            }

            var single = JsonSerializer.Deserialize<int>(output, JsonOptions);
            return single > 0 ? new HashSet<int>([single]) : new HashSet<int>();
        }
        catch
        {
            return new HashSet<int>();
        }
    }

    public Task WaitForPowerShellDirectAsync(string vmName, string username, string password, int timeoutSeconds = 180)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = {{Quote(vmName)}}
            $username = {{Quote(username)}}
            $plainPassword = {{Quote(password)}}
            $timeoutSeconds = {{Math.Clamp(timeoutSeconds, 30, 600).ToString(CultureInfo.InvariantCulture)}}
            $secure = ConvertTo-SecureString $plainPassword -AsPlainText -Force
            $credential = [pscredential]::new($username, $secure)
            $deadline = (Get-Date).AddSeconds($timeoutSeconds)
            $lastError = ''
            $lastErrorId = ''
            $credentialFailures = 0

            do {
                try {
                    $ready = Invoke-Command -VMName $vmName -Credential $credential -ErrorAction Stop -ScriptBlock { 'ready' }
                    if ([string]$ready -eq 'ready') { return }
                }
                catch {
                    $lastError = [string]$_.Exception.Message
                    $lastErrorId = [string]$_.FullyQualifiedErrorId
                    $credentialRejected =
                        ($lastErrorId -match 'InvalidCredential|Authentication') -or
                        ($lastError -match '(?i)credential.*invalid|username or password|user name or password|logon failure')

                    if ($credentialRejected) {
                        $credentialFailures++
                    }
                    else {
                        $credentialFailures = 0
                    }

                    if ($credentialFailures -ge 2) {
                        throw ('Windows is reachable through PowerShell Direct, but the bootstrap credential for user {0} was rejected. Check Admin -> OS Templates and save the password that is actually baked into this golden VHDX. PowerShell error: {1}' -f $username, $lastError)
                    }
                }

                Start-Sleep -Seconds 4
            }
            while ((Get-Date) -lt $deadline)

            throw ('PowerShell Direct did not become ready within {0} seconds. Last error ID: {1}. Last error: {2}' -f $timeoutSeconds, $lastErrorId, $lastError)
            """;

        return RunAsync(script);
    }

    public async Task<string> RollbackFailedReinstallAsync(
        string vmName,
        string freshDiskPath,
        string rollbackDiskPath,
        bool startAfter = true)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = {{Quote(vmName)}}
            $freshDiskPath = {{Quote(freshDiskPath)}}
            $rollbackDiskPath = {{Quote(rollbackDiskPath)}}
            $startAfter = ${{(startAfter ? "true" : "false")}}

            if (-not (Test-Path -LiteralPath $rollbackDiskPath)) {
                throw ('The previous OS rollback disk was not found: ' + $rollbackDiskPath)
            }

            $vm = Get-VM -Name $vmName -ErrorAction Stop
            if ([string]$vm.State -in @('Running','Starting','Resuming','Pausing')) {
                Stop-VM -Name $vmName -Force -Confirm:$false -ErrorAction Stop
            }

            $deadline = (Get-Date).AddSeconds(90)
            do {
                $vm = Get-VM -Name $vmName -ErrorAction Stop
                if ([string]$vm.State -notin @('Running','Starting','Resuming','Stopping','Pausing')) { break }
                Start-Sleep -Seconds 2
            }
            while ((Get-Date) -lt $deadline)

            $disk = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction Stop | Select-Object -First 1)[0]
            if ($null -eq $disk) { throw 'No primary VM disk was found during reinstall rollback.' }

            Set-VMHardDiskDrive -VMName $vmName `
                -ControllerType $disk.ControllerType `
                -ControllerNumber $disk.ControllerNumber `
                -ControllerLocation $disk.ControllerLocation `
                -Path $rollbackDiskPath `
                -ErrorAction Stop

            $vm = Get-VM -Name $vmName -ErrorAction Stop
            $vmIdentity = 'NT VIRTUAL MACHINE\' + [string]$vm.VMId
            $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
            & $icacls $rollbackDiskPath /grant ($vmIdentity + ':F') | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw ('Previous OS disk was reattached, but Hyper-V ACL repair failed with icacls exit code ' + $LASTEXITCODE)
            }

            if ($startAfter) {
                Start-VM -Name $vmName -ErrorAction Stop | Out-Null
            }

            if (-not [string]::IsNullOrWhiteSpace($freshDiskPath) -and (Test-Path -LiteralPath $freshDiskPath)) {
                try { Remove-Item -LiteralPath $freshDiskPath -Force -ErrorAction Stop } catch { }
            }

            $state = [string](Get-VM -Name $vmName -ErrorAction Stop).State
            [pscustomobject]@{
                VmName = $vmName
                RollbackDiskPath = $rollbackDiskPath
                State = $state
                Message = ('Previous OS disk was restored after Smart Reinstall setup failed. VM state: ' + $state)
            } | ConvertTo-Json -Compress
            """;

        var output = await RunJsonAsync(script);
        using var document = JsonDocument.Parse(output);
        return document.RootElement.TryGetProperty("Message", out var message)
            ? message.GetString() ?? "Previous OS disk restored."
            : "Previous OS disk restored.";
    }

    public async Task<VmReinstallRollbackDiskRestoreResult> RestoreReinstallRollbackDiskAsync(
        string vmName,
        string expectedReinstalledDiskPath,
        string rollbackDiskPath,
        bool startAfter = true)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = {{Quote(vmName)}}
            $expectedFresh = {{Quote(expectedReinstalledDiskPath)}}
            $rollbackPath = {{Quote(rollbackDiskPath)}}
            $startAfter = ${{(startAfter ? "true" : "false")}}

            if (-not (Test-Path -LiteralPath $rollbackPath)) {
                throw ('The protected previous OS disk was not found: ' + $rollbackPath)
            }

            $safeVm = ($vmName -replace '[\\/:*?"<>|]', '-').Trim()
            $rollbackFile = [System.IO.Path]::GetFileName($rollbackPath)
            $rollbackExtension = [System.IO.Path]::GetExtension($rollbackPath).ToLowerInvariant()
            $rollbackExpectedPrefix = $safeVm + '-reinstall-rollback-'
            $rollbackExtensionValid = $rollbackExtension -in @('.vhd','.vhdx')
            $rollbackNameValid = $rollbackFile.StartsWith($rollbackExpectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)
            if (-not $rollbackExtensionValid -or -not $rollbackNameValid) {
                throw ('Refusing to restore an unexpected rollback disk path: ' + $rollbackPath)
            }

            $checkpointCount = @(Get-VMSnapshot -VMName $vmName -ErrorAction SilentlyContinue).Count
            if ($checkpointCount -gt 0) {
                throw ('Previous OS restore is blocked while the VM has ' + $checkpointCount + ' Hyper-V checkpoint(s). Remove/merge them first.')
            }

            $rollbackVhd = Get-VHD -Path $rollbackPath -ErrorAction Stop
            if (-not [string]::IsNullOrWhiteSpace([string]$rollbackVhd.ParentPath)) {
                throw 'The protected previous OS disk unexpectedly became a differencing disk. Restore was blocked.'
            }

            $vm = Get-VM -Name $vmName -ErrorAction Stop
            $previousState = [string]$vm.State
            $wasRunning = $previousState -eq 'Running'

            $disk = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction Stop | Select-Object -First 1)[0]
            if ($null -eq $disk -or [string]::IsNullOrWhiteSpace([string]$disk.Path)) {
                throw 'No primary virtual hard disk was found on the VM.'
            }

            $currentPath = [string]$disk.Path
            $hasExpectedFresh = -not [string]::IsNullOrWhiteSpace($expectedFresh)
            $currentMatchesExpected = $true
            if ($hasExpectedFresh) {
                $currentFullPath = [System.IO.Path]::GetFullPath($currentPath)
                $expectedFullPath = [System.IO.Path]::GetFullPath($expectedFresh)
                $currentMatchesExpected = [string]::Equals($currentFullPath, $expectedFullPath, [System.StringComparison]::OrdinalIgnoreCase)
            }
            if ($hasExpectedFresh -and -not $currentMatchesExpected) {
                throw ('The VM primary disk changed after reinstall. Expected ' + $expectedFresh + ' but Hyper-V is using ' + $currentPath + '. Restore was blocked to avoid replacing an unexpected disk.')
            }

            if (-not (Test-Path -LiteralPath $currentPath)) {
                throw ('The current reinstalled OS disk is missing: ' + $currentPath)
            }

            $currentVhd = Get-VHD -Path $currentPath -ErrorAction Stop
            $currentExtension = [System.IO.Path]::GetExtension($currentPath).ToLowerInvariant()
            $currentHasParent = -not [string]::IsNullOrWhiteSpace([string]$currentVhd.ParentPath)
            if ($currentExtension -eq '.avhdx' -or $currentHasParent) {
                throw 'The current VM disk is part of a checkpoint/differencing chain. Merge/remove checkpoints before restoring the previous OS.'
            }

            if ([string]$vm.State -in @('Running','Starting','Resuming','Pausing')) {
                Stop-VM -Name $vmName -Force -Confirm:$false -ErrorAction Stop
            }

            $deadline = (Get-Date).AddSeconds(120)
            do {
                $state = [string](Get-VM -Name $vmName -ErrorAction Stop).State
                if ($state -notin @('Running','Stopping','Starting','Resuming','Pausing')) { break }
                Start-Sleep -Seconds 2
            } while ((Get-Date) -lt $deadline)

            $state = [string](Get-VM -Name $vmName -ErrorAction Stop).State
            if ($state -in @('Running','Stopping','Starting','Resuming','Pausing')) {
                throw ('The VM did not stop in time. Current state: ' + $state)
            }

            Set-VMHardDiskDrive -VMName $vmName `
                -ControllerType $disk.ControllerType `
                -ControllerNumber $disk.ControllerNumber `
                -ControllerLocation $disk.ControllerLocation `
                -Path $rollbackPath `
                -ErrorAction Stop

            $vm = Get-VM -Name $vmName -ErrorAction Stop
            $vmIdentity = 'NT VIRTUAL MACHINE\' + [string]$vm.VMId
            $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
            & $icacls $rollbackPath /grant ($vmIdentity + ':F') | Out-Null
            if ($LASTEXITCODE -ne 0) {
                try {
                    Set-VMHardDiskDrive -VMName $vmName `
                        -ControllerType $disk.ControllerType `
                        -ControllerNumber $disk.ControllerNumber `
                        -ControllerLocation $disk.ControllerLocation `
                        -Path $currentPath `
                        -ErrorAction SilentlyContinue
                } catch { }

                throw ('Previous OS disk was attached but its Hyper-V ACL could not be repaired. icacls exit code: ' + $LASTEXITCODE)
            }

            if ($startAfter) {
                try {
                    Start-VM -Name $vmName -ErrorAction Stop | Out-Null

                    $runDeadline = (Get-Date).AddSeconds(60)
                    do {
                        $state = [string](Get-VM -Name $vmName -ErrorAction Stop).State
                        if ($state -eq 'Running') { break }
                        if ($state -eq 'Off') { break }
                        Start-Sleep -Seconds 2
                    } while ((Get-Date) -lt $runDeadline)

                    if ([string](Get-VM -Name $vmName -ErrorAction Stop).State -ne 'Running') {
                        throw 'The previous OS VM did not reach the Running state.'
                    }
                }
                catch {
                    try {
                        $failedVm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
                        if ($failedVm -and [string]$failedVm.State -ne 'Off') {
                            Stop-VM -Name $vmName -TurnOff -Confirm:$false -ErrorAction SilentlyContinue
                        }

                        Set-VMHardDiskDrive -VMName $vmName `
                            -ControllerType $disk.ControllerType `
                            -ControllerNumber $disk.ControllerNumber `
                            -ControllerLocation $disk.ControllerLocation `
                            -Path $currentPath `
                            -ErrorAction Stop

                        Start-VM -Name $vmName -ErrorAction SilentlyContinue | Out-Null
                    } catch { }

                    throw ('The previous OS disk could not be started, so PenguinPanel reattached the current reinstalled OS. ' + [string]$_.Exception.Message)
                }
            }

            $attachedPaths = @(
                Get-VM -ErrorAction SilentlyContinue |
                ForEach-Object {
                    Get-VMHardDiskDrive -VMName $_.Name -ErrorAction SilentlyContinue |
                    ForEach-Object { [string]$_.Path }
                } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            )

            $freshStillAttached = @(
                $attachedPaths |
                Where-Object {
                    [string]::Equals(
                        [System.IO.Path]::GetFullPath($_),
                        [System.IO.Path]::GetFullPath($currentPath),
                        [System.StringComparison]::OrdinalIgnoreCase)
                }
            ).Count -gt 0

            if (-not $freshStillAttached -and (Test-Path -LiteralPath $currentPath)) {
                Remove-Item -LiteralPath $currentPath -Force -ErrorAction Stop
            }

            $finalState = [string](Get-VM -Name $vmName -ErrorAction Stop).State
            [pscustomobject]@{
                VmName = $vmName
                RestoredDiskPath = $rollbackPath
                RemovedReinstalledDiskPath = $currentPath
                PreviousState = $previousState
                FinalState = $finalState
                WasRunning = [bool]$wasRunning
                Message = ('Previous OS restored successfully. VM state: ' + $finalState)
            } | ConvertTo-Json -Compress -Depth 4
            """;

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<ReinstallRollbackRestoreJson>(
            output,
            JsonOptions)
            ?? throw new InvalidOperationException(
                "Previous OS restore returned no result.");

        return new VmReinstallRollbackDiskRestoreResult(
            item.VmName ?? vmName,
            item.RestoredDiskPath ?? rollbackDiskPath,
            item.RemovedReinstalledDiskPath ?? expectedReinstalledDiskPath,
            item.PreviousState ?? string.Empty,
            item.FinalState ?? string.Empty,
            item.WasRunning,
            item.Message ?? "Previous OS restored.");
    }

    public Task DeleteReinstallRollbackDiskAsync(
        string vmName,
        string rollbackDiskPath)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = {{Quote(vmName)}}
            $rollbackPath = {{Quote(rollbackDiskPath)}}

            if ([string]::IsNullOrWhiteSpace($rollbackPath)) {
                return
            }

            $safeVm = ($vmName -replace '[\\/:*?"<>|]', '-').Trim()
            $fileName = [System.IO.Path]::GetFileName($rollbackPath)
            $extension = [System.IO.Path]::GetExtension($rollbackPath).ToLowerInvariant()

            $expectedPrefix = $safeVm + '-reinstall-rollback-'
            $extensionValid = $extension -in @('.vhd','.vhdx')
            $nameValid = $fileName.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)
            if (-not $extensionValid -or -not $nameValid) {
                throw ('Refusing to delete an unexpected rollback disk path: ' + $rollbackPath)
            }

            if (-not (Test-Path -LiteralPath $rollbackPath)) {
                return
            }

            $attached = @(
                Get-VM -ErrorAction SilentlyContinue |
                ForEach-Object {
                    Get-VMHardDiskDrive -VMName $_.Name -ErrorAction SilentlyContinue
                } |
                Where-Object {
                    $candidatePath = [string]$_.Path
                    $samePath = $false
                    if (-not [string]::IsNullOrWhiteSpace($candidatePath)) {
                        $candidateFullPath = [System.IO.Path]::GetFullPath($candidatePath)
                        $rollbackFullPath = [System.IO.Path]::GetFullPath($rollbackPath)
                        $samePath = [string]::Equals($candidateFullPath, $rollbackFullPath, [System.StringComparison]::OrdinalIgnoreCase)
                    }
                    $samePath
                }
            )

            if ($attached.Count -gt 0) {
                throw 'Rollback disk is currently attached to a Hyper-V VM. Deletion was blocked.'
            }

            $vhd = Get-VHD -Path $rollbackPath -ErrorAction Stop
            if (-not [string]::IsNullOrWhiteSpace([string]$vhd.ParentPath)) {
                throw 'Rollback disk unexpectedly has a parent/differencing relationship. Deletion was blocked.'
            }

            Remove-Item -LiteralPath $rollbackPath -Force -ErrorAction Stop
            """;

        return RunAsync(script);
    }

    public Task ChangeWindowsGuestPasswordAsync(string vmName, string username, string currentPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("No Windows username is saved for this VM.");
        }

        if (string.IsNullOrWhiteSpace(currentPassword))
        {
            throw new InvalidOperationException("No current VM password is saved in the panel. Save the existing RDP password first.");
        }

        var script =
            "$vmName = " + Quote(vmName) + "; " +
            "$username = " + Quote(username) + "; " +
            "$currentPassword = ConvertTo-SecureString " + Quote(currentPassword) + " -AsPlainText -Force; " +
            "$credential = [pscredential]::new($username, $currentPassword); " +
            "$newPassword = " + Quote(newPassword) + "; " +
            "Invoke-Command -VMName $vmName -Credential $credential -ErrorAction Stop -ScriptBlock { " +
            "param($targetUser, $plainPassword) " +
            "$localUser = [string]$targetUser; " +
            "if ($localUser.Contains('\\')) { $localUser = $localUser.Split('\\')[-1] } " +
            "$securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force; " +
            "if (Get-Command Set-LocalUser -ErrorAction SilentlyContinue) { " +
            "Set-LocalUser -Name $localUser -Password $securePassword -ErrorAction Stop; " +
            "} else { " +
            "& net.exe user $localUser $plainPassword; " +
            "if ($LASTEXITCODE -ne 0) { throw 'net user failed to update the password.' } " +
            "} " +
            "} -ArgumentList $username, $newPassword | Out-Null";

        return RunAsync(script);
    }

    public async Task EnsureWindowsRecoveryAccountAsync(
        string vmName,
        string bootstrapUsername,
        string bootstrapPassword,
        string recoveryUsername,
        string recoveryPassword)
    {
        ValidateGuestCredentials(bootstrapUsername, bootstrapPassword);
        ValidateGuestCredentials(recoveryUsername, recoveryPassword);

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = {{Quote(vmName)}}
            $bootstrapUsername = {{Quote(bootstrapUsername)}}
            $bootstrapPassword = {{Quote(bootstrapPassword)}}
            $recoveryUsername = {{Quote(recoveryUsername)}}
            $recoveryPassword = {{Quote(recoveryPassword)}}

            $bootstrapSecure = ConvertTo-SecureString $bootstrapPassword -AsPlainText -Force
            $bootstrapCredential = [pscredential]::new($bootstrapUsername, $bootstrapSecure)

            $guestScript = {
                param($recoveryUsername, $recoveryPassword)

                $ErrorActionPreference = 'Stop'
                $stage = 'prepare recovery username'
                try {
                $localRecoveryUser = [string]$recoveryUsername
                if ($localRecoveryUser.Contains('\')) {
                    $localRecoveryUser = $localRecoveryUser.Split('\')[-1]
                }

                if ([string]::IsNullOrWhiteSpace($localRecoveryUser)) {
                    throw 'Recovery username cannot be empty.'
                }

                $stage = 'check Windows LocalAccounts module'
                if (-not (Get-Command Get-LocalUser -ErrorAction SilentlyContinue)) {
                    throw 'Windows LocalAccounts PowerShell cmdlets are required for agentless recovery.'
                }

                $stage = 'prepare recovery password'
                $secureRecoveryPassword = ConvertTo-SecureString $recoveryPassword -AsPlainText -Force
                $existing = Get-LocalUser -Name $localRecoveryUser -ErrorAction SilentlyContinue

                if ($null -eq $existing) {
                    $stage = 'create recovery local user'
                    New-LocalUser `
                        -Name $localRecoveryUser `
                        -Password $secureRecoveryPassword `
                        -Description 'PenguinPanel emergency recovery account' `
                        -PasswordNeverExpires `
                        -AccountNeverExpires `
                        -ErrorAction Stop | Out-Null
                }
                else {
                    $stage = 'update recovery local user'
                    Set-LocalUser `
                        -Name $localRecoveryUser `
                        -Password $secureRecoveryPassword `
                        -PasswordNeverExpires $true `
                        -UserMayChangePassword $false `
                        -Description 'PenguinPanel emergency recovery account' `
                        -ErrorAction Stop
                    Enable-LocalUser -Name $localRecoveryUser -ErrorAction Stop
                }

                $stage = 'configure recovery account policy'
                & net.exe user $localRecoveryUser /active:yes /expires:never /passwordchg:no | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    throw 'Windows could not configure the recovery account policy.'
                }

                $stage = 'resolve local Administrators group'
                $adminGroup = Get-LocalGroup -SID 'S-1-5-32-544' -ErrorAction Stop
                $isAdmin = @(
                    Get-LocalGroupMember -Group $adminGroup.Name -ErrorAction SilentlyContinue |
                    Where-Object {
                        $memberName = [string]$_.Name
                        $leaf = if ($memberName.Contains('\')) { $memberName.Split('\')[-1] } else { $memberName }
                        $leaf -eq $localRecoveryUser
                    }
                ).Count -gt 0

                if (-not $isAdmin) {
                    $stage = 'add recovery user to Administrators'
                    Add-LocalGroupMember -Group $adminGroup.Name -Member $localRecoveryUser -ErrorAction Stop
                }

                $stage = 'hide recovery account from sign-in list'
                $userListPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList'
                New-Item -Path $userListPath -Force | Out-Null
                New-ItemProperty `
                    -Path $userListPath `
                    -Name $localRecoveryUser `
                    -PropertyType DWord `
                    -Value 0 `
                    -Force | Out-Null
                }
                catch {
                    throw ('Agentless recovery failed at [' + $stage + ']: ' + $_.Exception.Message)
                }
            }

            $hostStage = 'bootstrap PowerShell Direct session'
            try {
            Invoke-Command `
                -VMName $vmName `
                -Credential $bootstrapCredential `
                -ScriptBlock $guestScript `
                -ArgumentList $recoveryUsername, $recoveryPassword `
                -ErrorAction Stop | Out-Null

            $hostStage = 'verify new PenguinRecovery credential'
            $recoverySecure = ConvertTo-SecureString $recoveryPassword -AsPlainText -Force
            $recoveryCredential = [pscredential]::new($recoveryUsername, $recoverySecure)
            $verified = Invoke-Command `
                -VMName $vmName `
                -Credential $recoveryCredential `
                -ScriptBlock { 'PenguinRecoveryReady' } `
                -ErrorAction Stop

            if ([string]$verified -ne 'PenguinRecoveryReady') {
                throw 'The recovery account was created but PowerShell Direct verification returned an unexpected result.'
            }
            }
            catch {
                if ($_.Exception.Message -like 'Agentless recovery failed at *') {
                    throw
                }
                throw ('Agentless recovery failed at [' + $hostStage + ']: ' + $_.Exception.Message)
            }
            """;

        await RunAsync(script);
    }

    public async Task<string> ResetWindowsGuestPasswordWithRecoveryAsync(
        string vmName,
        string recoveryUsername,
        string recoveryPassword,
        string targetUsername,
        string newTargetPassword,
        string rotatedRecoveryPassword,
        bool forceLogoffSessions,
        bool restartRemoteDesktopService)
    {
        ValidateGuestCredentials(recoveryUsername, recoveryPassword);
        if (string.IsNullOrWhiteSpace(targetUsername))
        {
            throw new InvalidOperationException("No Windows username was supplied for the emergency reset.");
        }

        if (string.IsNullOrWhiteSpace(newTargetPassword))
        {
            throw new InvalidOperationException("The new Windows password cannot be empty.");
        }

        ValidateGuestCredentials(recoveryUsername, rotatedRecoveryPassword);

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = {{Quote(vmName)}}
            $recoveryUsername = {{Quote(recoveryUsername)}}
            $recoveryPassword = {{Quote(recoveryPassword)}}
            $targetUsername = {{Quote(targetUsername)}}
            $newTargetPassword = {{Quote(newTargetPassword)}}
            $rotatedRecoveryPassword = {{Quote(rotatedRecoveryPassword)}}
            $forceLogoffSessions = ${{(forceLogoffSessions ? "true" : "false")}}
            $restartRemoteDesktopService = ${{(restartRemoteDesktopService ? "true" : "false")}}

            $recoverySecure = ConvertTo-SecureString $recoveryPassword -AsPlainText -Force
            $recoveryCredential = [pscredential]::new($recoveryUsername, $recoverySecure)

            $guestScript = {
                param(
                    $targetUsername,
                    $newTargetPassword,
                    $recoveryUsername,
                    $rotatedRecoveryPassword,
                    $forceLogoffSessions,
                    $restartRemoteDesktopService
                )

                $ErrorActionPreference = 'Stop'
                $targetUser = [string]$targetUsername
                if ($targetUser.Contains('\')) {
                    $targetUser = $targetUser.Split('\')[-1]
                }

                $recoveryUser = [string]$recoveryUsername
                if ($recoveryUser.Contains('\')) {
                    $recoveryUser = $recoveryUser.Split('\')[-1]
                }

                if ($targetUser -eq $recoveryUser) {
                    throw 'The customer Windows account cannot be the PenguinPanel recovery account.'
                }

                $target = Get-LocalUser -Name $targetUser -ErrorAction Stop
                $secureTargetPassword = ConvertTo-SecureString $newTargetPassword -AsPlainText -Force
                Set-LocalUser -Name $targetUser -Password $secureTargetPassword -ErrorAction Stop
                Enable-LocalUser -Name $targetUser -ErrorAction Stop

                $warnings = @()

                if ($restartRemoteDesktopService) {
                    try {
                        Restart-Service -Name 'TermService' -Force -ErrorAction Stop
                    }
                    catch {
                        $warnings += ('Remote Desktop service restart warning: ' + $_.Exception.Message)
                    }
                }

                if ($forceLogoffSessions) {
                    try {
                        $sessionLines = @(& quser.exe 2>$null | Select-Object -Skip 1)
                        foreach ($line in $sessionLines) {
                            $match = [regex]::Match([string]$line, '^\s*>?\s*\S+\s+(?:\S+\s+)?(?<id>\d+)\s+')
                            if ($match.Success) {
                                & logoff.exe ([int]$match.Groups['id'].Value) 2>$null
                            }
                        }
                    }
                    catch {
                        $warnings += ('RDP session logoff warning: ' + $_.Exception.Message)
                    }
                }

                $secureRotatedRecoveryPassword = ConvertTo-SecureString $rotatedRecoveryPassword -AsPlainText -Force
                Set-LocalUser `
                    -Name $recoveryUser `
                    -Password $secureRotatedRecoveryPassword `
                    -PasswordNeverExpires $true `
                    -UserMayChangePassword $false `
                    -ErrorAction Stop
                Enable-LocalUser -Name $recoveryUser -ErrorAction Stop

                [pscustomobject]@{
                    TargetUser = $targetUser
                    RecoveryUser = $recoveryUser
                    Warning = ($warnings -join ' | ')
                }
            }

            $result = Invoke-Command `
                -VMName $vmName `
                -Credential $recoveryCredential `
                -ScriptBlock $guestScript `
                -ArgumentList `
                    $targetUsername,
                    $newTargetPassword,
                    $recoveryUsername,
                    $rotatedRecoveryPassword,
                    $forceLogoffSessions,
                    $restartRemoteDesktopService `
                -ErrorAction Stop

            $newRecoverySecure = ConvertTo-SecureString $rotatedRecoveryPassword -AsPlainText -Force
            $newRecoveryCredential = [pscredential]::new($recoveryUsername, $newRecoverySecure)
            $verified = Invoke-Command `
                -VMName $vmName `
                -Credential $newRecoveryCredential `
                -ScriptBlock { 'PenguinRecoveryRotated' } `
                -ErrorAction Stop

            if ([string]$verified -ne 'PenguinRecoveryRotated') {
                throw 'The Windows password was changed, but the rotated recovery credential could not be verified.'
            }

            $result | ConvertTo-Json -Compress
            """;

        var output = await RunAsync(script);
        return string.IsNullOrWhiteSpace(output)
            ? "Windows password reset through Hyper-V PowerShell Direct."
            : output;
    }

    public async Task<VmRdpListenerStatus> GetWindowsGuestRdpListenerStatusAsync(string vmName, string username, string password)
    {
        ValidateGuestCredentials(username, password);

        var script = $$"""
            $ErrorActionPreference = 'Stop';
            $vmName = {{Quote(vmName)}};
            $username = {{Quote(username)}};
            $plainPassword = {{Quote(password)}};
            $securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force;
            $credential = [pscredential]::new($username, $securePassword);
            $guestScript = {
                $ErrorActionPreference = 'Stop';
                $rdpTcpPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp';
                $terminalServerPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server';
                $port = [int](Get-ItemPropertyValue -Path $rdpTcpPath -Name PortNumber -ErrorAction Stop);
                $deny = [int](Get-ItemPropertyValue -Path $terminalServerPath -Name fDenyTSConnections -ErrorAction Stop);
                $service = Get-Service -Name TermService -ErrorAction SilentlyContinue;
                $listening = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue).Count -gt 0;
                $udpListening = @(Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue).Count -gt 0;
                [pscustomobject]@{
                    Port = $port;
                    RemoteDesktopEnabled = ($deny -eq 0);
                    TermServiceRunning = ($null -ne $service -and [string]$service.Status -eq 'Running');
                    TcpListening = [bool]$listening;
                    UdpListening = [bool]$udpListening;
                    CheckedAt = (Get-Date).ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture);
                }
            };
            $result = Invoke-Command -VMName $vmName -Credential $credential -ScriptBlock $guestScript -ErrorAction Stop;
            $result | ConvertTo-Json -Compress -Depth 4;
            """;

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<VmRdpListenerJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Windows RDP listener check returned no result.");

        return new VmRdpListenerStatus(
            vmName,
            item.Port <= 0 ? 3389 : item.Port,
            item.RemoteDesktopEnabled,
            item.TermServiceRunning,
            item.TcpListening,
            item.UdpListening,
            ParseDateTime(item.CheckedAt));
    }

    public async Task<VmRdpListenerStatus> SetWindowsGuestRdpPortAsync(string vmName, string username, string password, int port)
    {
        ValidateGuestCredentials(username, password);
        if (!IsValidPort(port))
        {
            throw new InvalidOperationException("Windows RDP port must be between 1 and 65535.");
        }

        var script = $$"""
            $ErrorActionPreference = 'Stop';
            $ProgressPreference = 'SilentlyContinue';
            $vmName = {{Quote(vmName)}};
            $username = {{Quote(username)}};
            $plainPassword = {{Quote(password)}};
            $targetPort = {{port.ToString(CultureInfo.InvariantCulture)}};
            $securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force;
            $credential = [pscredential]::new($username, $securePassword);

            $guestScript = {
                param($newPort)
                $ErrorActionPreference = 'Stop';
                $ProgressPreference = 'SilentlyContinue';
                $rdpTcpPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp';
                $terminalServerPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server';
                $newPort = [int]$newPort;
                $oldPort = [int](Get-ItemPropertyValue -Path $rdpTcpPath -Name PortNumber -ErrorAction Stop);
                $changed = ($oldPort -ne $newPort);
                $tcpRuleName = 'PenguinPanel-RDP-Listener-TCP-' + $newPort;
                $udpRuleName = 'PenguinPanel-RDP-Listener-UDP-' + $newPort;
                $activeRuleNames = @($tcpRuleName, $udpRuleName);

                try {
                    # RDP establishes its reliable connection over TCP and can add a
                    # lower-latency UDP transport on the same listener port.
                    foreach ($ruleName in $activeRuleNames) {
                        Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue |
                            Remove-NetFirewallRule -ErrorAction SilentlyContinue;
                    }
                    New-NetFirewallRule -Name $tcpRuleName `
                        -DisplayName ('PenguinPanel RDP listener TCP ' + $newPort) `
                        -Group 'PenguinPanel RDP Listener' `
                        -Direction Inbound -Action Allow -Enabled True -Profile Any `
                        -Protocol TCP -LocalPort $newPort -ErrorAction Stop | Out-Null;
                    New-NetFirewallRule -Name $udpRuleName `
                        -DisplayName ('PenguinPanel RDP listener UDP ' + $newPort) `
                        -Group 'PenguinPanel RDP Listener' `
                        -Direction Inbound -Action Allow -Enabled True -Profile Any `
                        -Protocol UDP -LocalPort $newPort -ErrorAction Stop | Out-Null;

                    Set-ItemProperty -Path $terminalServerPath -Name fDenyTSConnections -Value 0 -ErrorAction Stop;
                    Set-ItemProperty -Path $rdpTcpPath -Name PortNumber -Value $newPort -ErrorAction Stop;
                }
                catch {
                    # Only roll back when writing the actual configuration failed. A service restart
                    # failure is handled by the Hyper-V host with a one-time VM restart fallback.
                    if ($changed -and $oldPort -gt 0) {
                        try {
                            Set-ItemProperty -Path $rdpTcpPath -Name PortNumber -Value $oldPort -ErrorAction SilentlyContinue;
                            foreach ($ruleName in $activeRuleNames) {
                                Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue |
                                    Remove-NetFirewallRule -ErrorAction SilentlyContinue;
                            }
                        } catch { }
                    }
                    throw;
                }

                $restartError = '';
                try {
                    $service = Get-Service -Name TermService -ErrorAction Stop;
                    if ($changed -and [string]$service.Status -eq 'Running') {
                        Restart-Service -Name TermService -Force -ErrorAction Stop;
                    } elseif ([string]$service.Status -ne 'Running') {
                        Start-Service -Name TermService -ErrorAction Stop;
                    }
                }
                catch {
                    # Some Windows Server images refuse to stop TermService. Keep the registry and
                    # firewall change in place so the host can reboot the VM and activate it safely.
                    $restartError = [string]$_.Exception.Message;
                }

                $deadline = (Get-Date).AddSeconds(15);
                $listening = $false;
                $udpListening = $false;
                do {
                    Start-Sleep -Milliseconds 500;
                    $service = Get-Service -Name TermService -ErrorAction SilentlyContinue;
                    $listening = @(Get-NetTCPConnection -State Listen -LocalPort $newPort -ErrorAction SilentlyContinue).Count -gt 0;
                    $udpListening = @(Get-NetUDPEndpoint -LocalPort $newPort -ErrorAction SilentlyContinue).Count -gt 0;
                    if ($listening -and $udpListening -and $null -ne $service -and [string]$service.Status -eq 'Running') { break }
                } while ((Get-Date) -lt $deadline);

                if ($listening) {
                    # Clean up older dedicated PenguinPanel RDP-listener rules after success.
                    @(Get-NetFirewallRule -Group 'PenguinPanel RDP Listener' -ErrorAction SilentlyContinue |
                        Where-Object { $activeRuleNames -notcontains [string]$_.Name }) |
                        Remove-NetFirewallRule -ErrorAction SilentlyContinue;
                }

                [pscustomobject]@{
                    Port = $newPort;
                    RemoteDesktopEnabled = $true;
                    TermServiceRunning = ($null -ne $service -and [string]$service.Status -eq 'Running');
                    TcpListening = [bool]$listening;
                    UdpListening = [bool]$udpListening;
                    RestartError = $restartError;
                    CheckedAt = (Get-Date).ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture);
                }
            };

            $result = Invoke-Command -VMName $vmName -Credential $credential -ScriptBlock $guestScript -ArgumentList $targetPort -ErrorAction Stop;
            $result | ConvertTo-Json -Compress -Depth 4;
            """;

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<VmRdpListenerJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Windows RDP port change returned no result.");

        var status = new VmRdpListenerStatus(
            vmName,
            item.Port <= 0 ? port : item.Port,
            item.RemoteDesktopEnabled,
            item.TermServiceRunning,
            item.TcpListening,
            item.UdpListening,
            ParseDateTime(item.CheckedAt));

        if (status.Port == port && status.IsReady)
        {
            return status;
        }

        // Windows Server can refuse Restart-Service/Stop-Service for TermService, especially while
        // the image is completing first-boot setup. The registry/firewall change is already saved,
        // so reboot the Hyper-V VM once, wait for PowerShell Direct, then verify the real listener.
        await RestartVmAsync(vmName);
        await Task.Delay(TimeSpan.FromSeconds(3));
        await WaitForPowerShellDirectAsync(vmName, username, password, 240);

        var verified = await GetWindowsGuestRdpListenerStatusAsync(vmName, username, password);
        if (verified.Port != port || !verified.IsReady)
        {
            var restartDetail = string.IsNullOrWhiteSpace(item.RestartError)
                ? string.Empty
                : $" TermService restart error before VM reboot: {item.RestartError}";
            throw new InvalidOperationException(
                $"Windows RDP was configured for TCP {port}, but it still was not ready after the VM restart fallback. " +
                $"Windows reports TCP {verified.Port}, service running={verified.TermServiceRunning}, listening={verified.TcpListening}.{restartDetail}");
        }

        await CleanupWindowsGuestRdpListenerRulesAsync(vmName, username, password, port);
        return verified;
    }

    private Task CleanupWindowsGuestRdpListenerRulesAsync(string vmName, string username, string password, int activePort)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop';
            $ProgressPreference = 'SilentlyContinue';
            $vmName = {{Quote(vmName)}};
            $username = {{Quote(username)}};
            $plainPassword = {{Quote(password)}};
            $activePort = {{activePort.ToString(CultureInfo.InvariantCulture)}};
            $securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force;
            $credential = [pscredential]::new($username, $securePassword);
            Invoke-Command -VMName $vmName -Credential $credential -ErrorAction Stop -ScriptBlock {
                param($port)
                $keepNames = @(
                    'PenguinPanel-RDP-Listener-TCP-' + [int]$port,
                    'PenguinPanel-RDP-Listener-UDP-' + [int]$port
                );
                @(Get-NetFirewallRule -Group 'PenguinPanel RDP Listener' -ErrorAction SilentlyContinue |
                    Where-Object { $keepNames -notcontains [string]$_.Name }) |
                    Remove-NetFirewallRule -ErrorAction SilentlyContinue;
            } -ArgumentList $activePort | Out-Null;
            """;
        return RunAsync(script);
    }

    public async Task<VmTemplateCreateResult> CreateTemplateFromVmDiskAsync(string vmName, string destinationFolder, string templateName)
    {
        var script =
            "$vmName = " + Quote(vmName) + "; " +
            "$destinationFolder = " + Quote(destinationFolder) + "; " +
            "$templateName = " + Quote(templateName) + "; " +
            "$vm = Get-VM -Name $vmName -ErrorAction Stop; " +
            "$previousState = [string]$vm.State; " +
            "$wasRunning = $previousState -in @('Running','Starting','Resuming'); " +
            "try { " +
            "if ($wasRunning) { Stop-VM -Name $vmName -Force -Confirm:$false -ErrorAction Stop; " +
            "$deadline = (Get-Date).AddSeconds(120); do { $state = [string](Get-VM -Name $vmName -ErrorAction Stop).State; if ($state -notin @('Running','Stopping','Starting','Resuming','Pausing')) { break }; Start-Sleep -Seconds 2 } while ((Get-Date) -lt $deadline); " +
            "$state = [string](Get-VM -Name $vmName -ErrorAction Stop).State; if ($state -in @('Running','Stopping','Starting','Resuming','Pausing')) { throw \"The VM did not stop in time. Current state: $state\" } } " +
            "$disk = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction Stop | Select-Object -First 1)[0]; " +
            "if ($null -eq $disk) { throw \"No virtual hard disk was found on this VM.\" } " +
            "$source = [string]$disk.Path; if ([string]::IsNullOrWhiteSpace($source) -or -not (Test-Path -LiteralPath $source)) { throw \"The source disk was not found: $source\" } " +
            "New-Item -Path $destinationFolder -ItemType Directory -Force | Out-Null; " +
            "$safeName = ($templateName -replace '[\\/:*?\"<>|]', '-').Trim(); if ([string]::IsNullOrWhiteSpace($safeName)) { $safeName = ($vmName -replace '[\\/:*?\"<>|]', '-') + '-template' } " +
            "$ext = [System.IO.Path]::GetExtension($source); if ([string]::IsNullOrWhiteSpace($ext)) { $ext = '.vhdx' } " +
            "$dest = Join-Path $destinationFolder ($safeName + $ext); $i = 2; while (Test-Path -LiteralPath $dest) { $dest = Join-Path $destinationFolder ($safeName + '-' + $i + $ext); $i++ } " +
            "Copy-Item -LiteralPath $source -Destination $dest -Force -ErrorAction Stop; " +
            "[pscustomobject]@{ TemplatePath = $dest; WasRunning = $wasRunning; Generation = [int]$vm.Generation; Message = \"Template created from $vmName primary disk. Source VM was stopped while copying and then restored to its previous power state.\" } | ConvertTo-Json -Compress -Depth 4 " +
            "} finally { if ($wasRunning) { Start-VM -Name $vmName -ErrorAction SilentlyContinue | Out-Null } }";

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<TemplateCreateJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Template creation returned no result.");
        return new VmTemplateCreateResult(
            result.TemplatePath ?? string.Empty,
            result.WasRunning,
            Math.Clamp(result.Generation, 1, 2),
            result.Message ?? "Template created.");
    }

    public async Task<VmTemplateValidationSandbox> CreateVmTemplateValidationSandboxAsync(
        string templatePath,
        string templateName,
        int generation,
        string workingRoot,
        int memoryMb,
        int processorCount,
        bool enableSecureBoot)
    {
        generation = Math.Clamp(generation, 1, 2);
        memoryMb = Math.Clamp(memoryMb, 1024, 8192);
        processorCount = Math.Clamp(processorCount, 1, 4);

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $templatePath = {{Quote(templatePath)}}
            $templateName = {{Quote(templateName)}}
            $workingRoot = {{Quote(workingRoot)}}
            $generation = {{generation.ToString(CultureInfo.InvariantCulture)}}
            $memoryMb = {{memoryMb.ToString(CultureInfo.InvariantCulture)}}
            $processorCount = {{processorCount.ToString(CultureInfo.InvariantCulture)}}
            $enableSecureBoot = ${{(enableSecureBoot ? "true" : "false")}}

            if (-not (Test-Path -LiteralPath $templatePath)) {
                throw ('Template VHD/VHDX was not found: ' + $templatePath)
            }

            $extension = [System.IO.Path]::GetExtension($templatePath).ToLowerInvariant()
            if ($extension -notin @('.vhd','.vhdx')) {
                throw 'Template validation supports VHD/VHDX files only.'
            }

            $templateVhd = Get-VHD -Path $templatePath -ErrorAction Stop
            if (-not [string]::IsNullOrWhiteSpace([string]$templateVhd.ParentPath)) {
                throw 'Template is a differencing VHD/VHDX. Validation requires a standalone golden-image disk.'
            }

            $templateFile = Get-Item -LiteralPath $templatePath -ErrorAction Stop
            $testId = [guid]::NewGuid().ToString('N').Substring(0, 12)
            $testVmName = 'PenguinTemplateTest-' + $testId
            $testFolder = Join-Path $workingRoot $testVmName
            $testDisk = Join-Path $testFolder ('validation' + $extension)
            $vmCreated = $false

            try {
                New-Item -ItemType Directory -Path $testFolder -Force | Out-Null

                $rootPath = [System.IO.Path]::GetPathRoot($testFolder)
                if (-not [string]::IsNullOrWhiteSpace($rootPath)) {
                    $drive = New-Object System.IO.DriveInfo($rootPath)
                    if ($drive.AvailableFreeSpace -lt 2GB) {
                        throw ('Template validation working drive has less than 2 GB free: ' + $rootPath)
                    }
                }

                New-VHD -Path $testDisk -ParentPath $templatePath -Differencing -ErrorAction Stop | Out-Null

                New-VM `
                    -Name $testVmName `
                    -Generation $generation `
                    -MemoryStartupBytes ($memoryMb * 1MB) `
                    -VHDPath $testDisk `
                    -Path $workingRoot `
                    -ErrorAction Stop | Out-Null
                $vmCreated = $true

                Set-VMProcessor -VMName $testVmName -Count $processorCount -ErrorAction Stop
                try { Set-VM -VMName $testVmName -CheckpointType Disabled -ErrorAction Stop } catch { }
                try { Set-VM -VMName $testVmName -AutomaticCheckpointsEnabled $false -ErrorAction Stop } catch { }
                Set-VM -VMName $testVmName -AutomaticStartAction Nothing -AutomaticStopAction TurnOff -ErrorAction Stop

                Get-VMNetworkAdapter -VMName $testVmName -ErrorAction SilentlyContinue |
                    Remove-VMNetworkAdapter -Confirm:$false -ErrorAction SilentlyContinue

                if ($generation -eq 2 -and $enableSecureBoot) {
                    Set-VMFirmware `
                        -VMName $testVmName `
                        -EnableSecureBoot On `
                        -SecureBootTemplate MicrosoftWindows `
                        -ErrorAction Stop
                }

                Start-VM -Name $testVmName -ErrorAction Stop | Out-Null

                [pscustomobject]@{
                    VmName = $testVmName
                    WorkingDirectory = $testFolder
                    DifferencingDiskPath = $testDisk
                    VirtualDiskBytes = [int64]$templateVhd.Size
                    PhysicalDiskBytes = [int64]$templateVhd.FileSize
                    TemplateLastWriteUtcTicks = [int64]$templateFile.LastWriteTimeUtc.Ticks
                    Generation = [int]$generation
                } | ConvertTo-Json -Compress -Depth 4
            }
            catch {
                if ($vmCreated) {
                    try {
                        $testVm = Get-VM -Name $testVmName -ErrorAction SilentlyContinue
                        if ($testVm -and [string]$testVm.State -notin @('Off','Stopped')) {
                            Stop-VM -Name $testVmName -TurnOff -Force -Confirm:$false -ErrorAction SilentlyContinue
                        }
                    } catch { }
                    try { Remove-VM -Name $testVmName -Force -ErrorAction SilentlyContinue } catch { }
                }

                try {
                    if (Test-Path -LiteralPath $testFolder) {
                        Remove-Item -LiteralPath $testFolder -Recurse -Force -ErrorAction SilentlyContinue
                    }
                } catch { }

                throw
            }
            """;

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<TemplateValidationSandboxJson>(
            output,
            JsonOptions)
            ?? throw new InvalidOperationException(
                "Template validation sandbox returned no result.");

        return new VmTemplateValidationSandbox(
            item.VmName ?? string.Empty,
            item.WorkingDirectory ?? string.Empty,
            item.DifferencingDiskPath ?? string.Empty,
            item.VirtualDiskBytes,
            item.PhysicalDiskBytes,
            item.TemplateLastWriteUtcTicks,
            Math.Clamp(item.Generation, 1, 2));
    }

    public async Task<VmTemplateGuestValidationInfo> GetVmTemplateGuestValidationInfoAsync(
        string vmName,
        string username,
        string password)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = {{Quote(vmName)}}
            $username = {{Quote(username)}}
            $plainPassword = {{Quote(password)}}
            $secure = ConvertTo-SecureString $plainPassword -AsPlainText -Force
            $credential = [pscredential]::new($username, $secure)

            $result = Invoke-Command `
                -VMName $vmName `
                -Credential $credential `
                -ErrorAction Stop `
                -ScriptBlock {
                    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
                    $termService = Get-Service TermService -ErrorAction SilentlyContinue
                    $rdpReg = Get-ItemProperty `
                        'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' `
                        -ErrorAction SilentlyContinue

                    [pscustomobject]@{
                        ComputerName = [string]$env:COMPUTERNAME
                        WindowsProductName = [string]$os.Caption
                        WindowsVersion = [string]$os.Version
                        BuildNumber = [string]$os.BuildNumber
                        Architecture = [string]$os.OSArchitecture
                        RdpServiceFound = [bool]($null -ne $termService)
                        RdpEnabled = [bool]($null -ne $rdpReg -and [int]$rdpReg.fDenyTSConnections -eq 0)
                        RdpServiceStatus = if ($termService) { [string]$termService.Status } else { 'Not found' }
                    }
                }

            $result | ConvertTo-Json -Compress -Depth 4
            """;

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<TemplateValidationGuestJson>(
            output,
            JsonOptions)
            ?? throw new InvalidOperationException(
                "Template guest validation returned no result.");

        return new VmTemplateGuestValidationInfo(
            item.ComputerName ?? string.Empty,
            item.WindowsProductName ?? string.Empty,
            item.WindowsVersion ?? string.Empty,
            item.BuildNumber ?? string.Empty,
            item.Architecture ?? string.Empty,
            item.RdpServiceFound,
            item.RdpEnabled,
            item.RdpServiceStatus ?? string.Empty);
    }

    public Task CleanupVmTemplateValidationSandboxAsync(
        string vmName,
        string workingDirectory,
        string configuredWorkingRoot)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = {{Quote(vmName)}}
            $workingDirectory = {{Quote(workingDirectory)}}
            $configuredRoot = {{Quote(configuredWorkingRoot)}}

            if (-not $vmName.StartsWith('PenguinTemplateTest-', [System.StringComparison]::OrdinalIgnoreCase)) {
                throw ('Refusing to clean an unexpected validation VM name: ' + $vmName)
            }

            $rootFull = [System.IO.Path]::GetFullPath($configuredRoot).TrimEnd('\') + '\'
            $workFull = [System.IO.Path]::GetFullPath($workingDirectory).TrimEnd('\') + '\'
            if (-not $workFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw ('Refusing to remove validation data outside configured root: ' + $workingDirectory)
            }

            $vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
            if ($vm) {
                if ([string]$vm.State -notin @('Off','Stopped')) {
                    Stop-VM -Name $vmName -TurnOff -Force -Confirm:$false -ErrorAction SilentlyContinue
                }
                Remove-VM -Name $vmName -Force -ErrorAction Stop
            }

            if (Test-Path -LiteralPath $workingDirectory) {
                Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction Stop
            }
            """;

        return RunAsync(script);
    }

    public async Task<VmReinstallPreflight> GetVmReinstallPreflightAsync(
        string vmName,
        string templateName,
        string templateDiskPath)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = {{Quote(vmName)}}
            $templateName = {{Quote(templateName)}}
            $templateDiskPath = {{Quote(templateDiskPath)}}

            if (-not (Test-Path -LiteralPath $templateDiskPath)) {
                throw ('Template disk was not found: ' + $templateDiskPath)
            }

            $templateExtension = [System.IO.Path]::GetExtension($templateDiskPath).ToLowerInvariant()
            if ($templateExtension -notin @('.vhd','.vhdx')) {
                throw 'Automatic reinstall supports VHD/VHDX templates only.'
            }

            $vm = Get-VM -Name $vmName -ErrorAction Stop
            $disk = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction Stop | Select-Object -First 1)[0]
            if ($null -eq $disk) { throw 'No primary virtual hard disk was found on this VM.' }

            $currentPath = [string]$disk.Path
            if (-not (Test-Path -LiteralPath $currentPath)) {
                throw ('Current VM disk is missing: ' + $currentPath)
            }

            $currentVhd = Get-VHD -Path $currentPath -ErrorAction Stop
            $templateVhd = Get-VHD -Path $templateDiskPath -ErrorAction Stop
            $checkpointCount = @(Get-VMSnapshot -VMName $vmName -ErrorAction SilentlyContinue).Count

            if ($checkpointCount -eq 0) {
                $mergeDeadline = (Get-Date).AddSeconds(20)
                while ((Get-Date) -lt $mergeDeadline) {
                    $currentExtension = [System.IO.Path]::GetExtension($currentPath).ToLowerInvariant()
                    $currentParent = [string]$currentVhd.ParentPath
                    if ($currentExtension -ne '.avhdx' -and [string]::IsNullOrWhiteSpace($currentParent)) {
                        break
                    }

                    Start-Sleep -Seconds 2
                    $disk = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction Stop | Select-Object -First 1)[0]
                    if ($null -eq $disk) { throw 'No primary virtual hard disk was found while waiting for a checkpoint merge.' }

                    $currentPath = [string]$disk.Path
                    if (-not (Test-Path -LiteralPath $currentPath)) {
                        continue
                    }

                    $currentVhd = Get-VHD -Path $currentPath -ErrorAction Stop
                }
            }

            $currentExtension = [System.IO.Path]::GetExtension($currentPath).ToLowerInvariant()
            $currentParentPath = [string]$currentVhd.ParentPath
            $currentIsDifferencing = ($currentExtension -eq '.avhdx') -or (-not [string]::IsNullOrWhiteSpace($currentParentPath))

            $targetFolder = Split-Path -Parent $currentPath
            $volumeRoot = [System.IO.Path]::GetPathRoot($targetFolder)
            $drive = New-Object System.IO.DriveInfo($volumeRoot)

            [pscustomobject]@{
                VmName = $vmName
                TemplateName = $templateName
                CurrentDiskPath = $currentPath
                CurrentVirtualDiskBytes = [int64]$currentVhd.Size
                CurrentPhysicalDiskBytes = [int64]$currentVhd.FileSize
                CurrentDiskParentPath = $currentParentPath
                CurrentDiskIsDifferencing = [bool]$currentIsDifferencing
                TemplateVirtualDiskBytes = [int64]$templateVhd.Size
                TemplatePhysicalDiskBytes = [int64]$templateVhd.FileSize
                TemplateParentPath = [string]$templateVhd.ParentPath
                CheckpointCount = [int]$checkpointCount
                TargetFreeBytes = [int64]$drive.AvailableFreeSpace
                TargetTotalBytes = [int64]$drive.TotalSize
            } | ConvertTo-Json -Compress
            """;

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<ReinstallPreflightJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Reinstall preflight returned no result.");

        var warnings = new List<string>();
        var canProceed = true;

        if (item.CheckpointCount > 0)
        {
            canProceed = false;
            warnings.Add($"VM has {item.CheckpointCount} Quick Restore Point/checkpoint(s). Remove or merge them before reinstall.");
        }

        if (item.CurrentDiskIsDifferencing)
        {
            canProceed = false;
            warnings.Add(
                "The active VM disk is still a Hyper-V differencing/checkpoint disk "
                + $"({item.CurrentDiskPath}). Hyper-V may still be merging a recently deleted restore point. "
                + "Wait for the merge to finish and retry. Reinstall will not touch the VM while the active disk is AVHDX/differencing.");
        }

        if (!string.IsNullOrWhiteSpace(item.TemplateParentPath))
        {
            canProceed = false;
            warnings.Add("Template is a differencing VHD/VHDX. Reinstall requires a standalone golden-image disk.");
        }

        if (item.TemplateVirtualDiskBytes > item.CurrentVirtualDiskBytes)
        {
            canProceed = false;
            warnings.Add(
                $"Template virtual size ({FormatByteCount(item.TemplateVirtualDiskBytes)}) is larger than the VPS disk ({FormatByteCount(item.CurrentVirtualDiskBytes)}).");
        }

        if (item.TargetFreeBytes < item.TemplatePhysicalDiskBytes)
        {
            canProceed = false;
            warnings.Add(
                $"VM storage has {FormatByteCount(item.TargetFreeBytes)} free but the template copy may need about {FormatByteCount(item.TemplatePhysicalDiskBytes)}.");
        }

        return new VmReinstallPreflight(
            item.VmName ?? vmName,
            item.TemplateName ?? templateName,
            item.CurrentDiskPath ?? string.Empty,
            item.CurrentVirtualDiskBytes,
            item.CurrentPhysicalDiskBytes,
            item.TemplateVirtualDiskBytes,
            item.TemplatePhysicalDiskBytes,
            item.CheckpointCount,
            item.TargetFreeBytes,
            item.TargetTotalBytes,
            0,
            false,
            canProceed,
            warnings,
            canProceed
                ? "Template and Hyper-V disk state passed the basic reinstall preflight."
                : "Reinstall preflight found blocking issues.");
    }

    public async Task<VmReinstallResult> ReinstallVmFromTemplateDiskAsync(
        string vmName,
        string templateName,
        string templateDiskPath,
        bool startAfter = true)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = {{Quote(vmName)}}
            $templateName = {{Quote(templateName)}}
            $templateDiskPath = {{Quote(templateDiskPath)}}
            $startAfter = ${{(startAfter ? "true" : "false")}}

            if (-not (Test-Path -LiteralPath $templateDiskPath)) {
                throw ('Template disk was not found: ' + $templateDiskPath)
            }

            $templateExt = [System.IO.Path]::GetExtension($templateDiskPath).ToLowerInvariant()
            if ($templateExt -notin @('.vhd','.vhdx')) {
                throw 'Automatic reinstall supports VHD/VHDX templates only.'
            }

            $vm = Get-VM -Name $vmName -ErrorAction Stop
            $previousState = [string]$vm.State
            $wasRunning = $previousState -in @('Running','Starting','Resuming')

            $snapshots = @(Get-VMSnapshot -VMName $vmName -ErrorAction SilentlyContinue)
            if ($snapshots.Count -gt 0) {
                throw ('Reinstall is blocked while ' + $snapshots.Count + ' Quick Restore Point/checkpoint(s) exist. Remove or merge them first.')
            }

            $disk = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction Stop | Select-Object -First 1)[0]
            if ($null -eq $disk) { throw 'No primary virtual hard disk was found on this VM.' }

            $oldPath = [string]$disk.Path
            if (-not (Test-Path -LiteralPath $oldPath)) {
                throw ('Current VM disk is missing: ' + $oldPath)
            }

            $oldVhd = Get-VHD -Path $oldPath -ErrorAction Stop
            $templateVhd = Get-VHD -Path $templateDiskPath -ErrorAction Stop

            $oldExtension = [System.IO.Path]::GetExtension($oldPath).ToLowerInvariant()
            $oldParentPath = [string]$oldVhd.ParentPath
            if ($oldExtension -eq '.avhdx' -or -not [string]::IsNullOrWhiteSpace($oldParentPath)) {
                throw ('Reinstall is blocked because the active VM disk is still a Hyper-V differencing/checkpoint disk: {0}. Wait for Hyper-V to finish merging restore points, then retry.' -f $oldPath)
            }

            if (-not [string]::IsNullOrWhiteSpace([string]$templateVhd.ParentPath)) {
                throw 'Reinstall template must be a standalone VHD/VHDX, not a differencing disk.'
            }

            if ([int64]$templateVhd.Size -gt [int64]$oldVhd.Size) {
                $templateGb = [math]::Round($templateVhd.Size / 1GB, 1)
                $vpsGb = [math]::Round($oldVhd.Size / 1GB, 1)
                throw ('Template virtual disk is larger than the purchased VPS disk. Template={0} GB; VPS={1} GB.' -f $templateGb, $vpsGb)
            }

            if ($wasRunning) {
                Stop-VM -Name $vmName -Force -Confirm:$false -ErrorAction Stop
            }

            $deadline = (Get-Date).AddSeconds(120)
            do {
                $state = [string](Get-VM -Name $vmName -ErrorAction Stop).State
                if ($state -notin @('Running','Stopping','Starting','Resuming','Pausing')) { break }
                Start-Sleep -Seconds 2
            } while ((Get-Date) -lt $deadline)

            $state = [string](Get-VM -Name $vmName -ErrorAction Stop).State
            if ($state -in @('Running','Stopping','Starting','Resuming','Pausing')) {
                throw ('The VM did not stop in time. Current state: ' + $state)
            }

            $disk = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction Stop | Select-Object -First 1)[0]
            if ($null -eq $disk) {
                if ($wasRunning) { Start-VM -Name $vmName -ErrorAction SilentlyContinue | Out-Null }
                throw 'Primary VM disk disappeared after the VM was stopped.'
            }

            $oldPath = [string]$disk.Path
            if (-not (Test-Path -LiteralPath $oldPath)) {
                if ($wasRunning) { Start-VM -Name $vmName -ErrorAction SilentlyContinue | Out-Null }
                throw ('Primary VM disk path does not exist after stopping the VM: ' + $oldPath)
            }

            $oldVhd = Get-VHD -Path $oldPath -ErrorAction Stop
            $oldExtension = [System.IO.Path]::GetExtension($oldPath).ToLowerInvariant()
            $oldParentPath = [string]$oldVhd.ParentPath

            if ($oldExtension -eq '.avhdx' -or -not [string]::IsNullOrWhiteSpace($oldParentPath)) {
                if ($wasRunning) { Start-VM -Name $vmName -ErrorAction SilentlyContinue | Out-Null }
                throw ('Hyper-V checkpoint merge is still active after stopping the VM. Active disk: {0}. No reinstall changes were made; wait for the merge to finish and retry.' -f $oldPath)
            }

            $targetFolder = Split-Path -Parent $oldPath
            if ([string]::IsNullOrWhiteSpace($targetFolder) -or -not (Test-Path -LiteralPath $targetFolder)) {
                throw ('Current VM disk folder is missing: ' + $targetFolder)
            }

            $safeVm = ($vmName -replace '[\\/:*?"<>|]', '-').Trim()
            $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            $oldExt = [System.IO.Path]::GetExtension($oldPath).ToLowerInvariant()
            if ($oldExt -notin @('.vhd','.vhdx')) { $oldExt = '.vhdx' }

            $newDiskPath = Join-Path $targetFolder ($safeVm + '-reinstall-' + $timestamp + $oldExt)
            $rollbackPath = Join-Path $targetFolder ($safeVm + '-reinstall-rollback-' + $timestamp + [System.IO.Path]::GetExtension($oldPath))

            if (Test-Path -LiteralPath $newDiskPath) {
                throw ('Reinstall staging path already exists: ' + $newDiskPath)
            }
            if (Test-Path -LiteralPath $rollbackPath) {
                throw ('Reinstall rollback path already exists: ' + $rollbackPath)
            }

            if ($templateExt -eq $oldExt) {
                Copy-Item -LiteralPath $templateDiskPath -Destination $newDiskPath -Force -ErrorAction Stop
            }
            else {
                Convert-VHD -Path $templateDiskPath -DestinationPath $newDiskPath -VHDType Dynamic -ErrorAction Stop
            }

            $newVhd = Get-VHD -Path $newDiskPath -ErrorAction Stop
            if ([int64]$newVhd.Size -lt [int64]$oldVhd.Size) {
                Resize-VHD -Path $newDiskPath -SizeBytes ([int64]$oldVhd.Size) -ErrorAction Stop
            }

            $vmIdentity = 'NT VIRTUAL MACHINE\' + [string]$vm.VMId
            $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
            & $icacls $newDiskPath /grant ($vmIdentity + ':F') | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw ('Failed to grant Hyper-V disk permission to ' + $vmIdentity + '. icacls exit code: ' + $LASTEXITCODE)
            }

            $switched = $false
            $oldMoved = $false

            try {
                Set-VMHardDiskDrive -VMName $vmName `
                    -ControllerType $disk.ControllerType `
                    -ControllerNumber $disk.ControllerNumber `
                    -ControllerLocation $disk.ControllerLocation `
                    -Path $newDiskPath `
                    -ErrorAction Stop
                $switched = $true

                if (-not (Test-Path -LiteralPath $oldPath)) {
                    throw ('Original OS disk disappeared after switching Hyper-V to the fresh disk: {0}. PenguinPanel will attempt to restore the original attachment before returning the error.' -f $oldPath)
                }

                Move-Item -LiteralPath $oldPath -Destination $rollbackPath -Force -ErrorAction Stop
                $oldMoved = $true

                if ($startAfter) {
                    try {
                        Start-VM -Name $vmName -ErrorAction Stop
                    }
                    catch {
                        Set-VMHardDiskDrive -VMName $vmName `
                            -ControllerType $disk.ControllerType `
                            -ControllerNumber $disk.ControllerNumber `
                            -ControllerLocation $disk.ControllerLocation `
                            -Path $rollbackPath `
                            -ErrorAction Stop

                        if (Test-Path -LiteralPath $newDiskPath) {
                            Remove-Item -LiteralPath $newDiskPath -Force -ErrorAction SilentlyContinue
                        }

                        try {
                            Start-VM -Name $vmName -ErrorAction Stop
                        }
                        catch { }

                        $bootError = [string]$_.Exception.Message
                        throw ('New reinstall disk failed to boot. PenguinPanel reattached the original OS disk at {0}. {1}' -f $rollbackPath, $bootError)
                    }
                }
            }
            catch {
                if (-not $switched -and (Test-Path -LiteralPath $newDiskPath)) {
                    Remove-Item -LiteralPath $newDiskPath -Force -ErrorAction SilentlyContinue
                }

                if ($switched -and -not $oldMoved) {
                    try {
                        Set-VMHardDiskDrive -VMName $vmName `
                            -ControllerType $disk.ControllerType `
                            -ControllerNumber $disk.ControllerNumber `
                            -ControllerLocation $disk.ControllerLocation `
                            -Path $oldPath `
                            -ErrorAction Stop

                        if (Test-Path -LiteralPath $newDiskPath) {
                            Remove-Item -LiteralPath $newDiskPath -Force -ErrorAction SilentlyContinue
                        }
                    }
                    catch { }
                }

                throw
            }

            $finalState = [string](Get-VM -Name $vmName -ErrorAction Stop).State
            $finalVhd = Get-VHD -Path $newDiskPath -ErrorAction Stop

            [pscustomobject]@{
                VmName = $vmName
                TemplateName = $templateName
                TemplatePath = $templateDiskPath
                NewDiskPath = $newDiskPath
                OldDiskRollbackPath = $rollbackPath
                DiskSizeBytes = [int64]$finalVhd.Size
                WasRunning = $wasRunning
                PreviousState = $previousState
                FinalState = $finalState
                Message = ('Reinstall disk swap completed from template {0}. Purchased virtual disk size was preserved. Previous OS disk is retained as rollback at {1}.' -f $templateName, $rollbackPath)
            } | ConvertTo-Json -Compress -Depth 4
            """;

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<ReinstallResultJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Reinstall returned no result.");

        return new VmReinstallResult(
            result.VmName ?? vmName,
            result.TemplateName ?? templateName,
            result.TemplatePath ?? templateDiskPath,
            result.NewDiskPath ?? string.Empty,
            result.OldDiskRollbackPath ?? string.Empty,
            result.DiskSizeBytes,
            result.WasRunning,
            result.PreviousState ?? string.Empty,
            result.FinalState ?? string.Empty,
            false,
            string.Empty,
            [],
            result.Message ?? "Reinstall disk swap completed.");
    }

    public async Task<ConsistencyHostInventory> GetConsistencyHostInventoryAsync(
        string natName,
        IReadOnlyList<string> storageRoots)
    {
        natName = (natName ?? string.Empty).Trim();
        var roots = (storageRoots ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rootsPs = "@(" + string.Join(",", roots.Select(Quote)) + ")";

        var script =
            "$ErrorActionPreference = 'Stop'; " +
            "$natName = " + Quote(natName) + "; " +
            "$storageRoots = " + rootsPs + "; " +

            "$vmItems = @(); " +
            "foreach ($vm in @(Get-VM -ErrorAction Stop)) { " +
            "$diskPaths = @(Get-VMHardDiskDrive -VMName $vm.Name -ErrorAction SilentlyContinue | ForEach-Object { [string]$_.Path } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }); " +
            "$vmItems += [pscustomobject]@{ Name=[string]$vm.Name; State=[string]$vm.State; VmPath=[string]$vm.Path; DiskPaths=@($diskPaths) }; " +
            "} " +

            "$nat = if ([string]::IsNullOrWhiteSpace($natName)) { @(Get-NetNat -ErrorAction SilentlyContinue | Select-Object -First 1)[0] } else { Get-NetNat -Name $natName -ErrorAction SilentlyContinue }; " +
            "$natExists = ($null -ne $nat); " +
            "if ($natExists) { $natName = [string]$nat.Name } " +
            "$natItems = @(); " +
            "if ($natExists) { " +
            "foreach ($map in @(Get-NetNatStaticMapping -NatName $natName -ErrorAction SilentlyContinue)) { " +
            "$natItems += [pscustomobject]@{ NatName=$natName; Protocol=([string]$map.Protocol).ToUpperInvariant(); ExternalIpAddress=[string]$map.ExternalIPAddress; ExternalPort=[int]$map.ExternalPort; InternalIpAddress=[string]$map.InternalIPAddress; InternalPort=[int]$map.InternalPort }; " +
            "} " +
            "} " +

            "$firewallItems = @(); " +
            "$managedRules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { " +
            "([string]$_.DisplayName).StartsWith('[Penguin VPS] ', [System.StringComparison]::OrdinalIgnoreCase) -or " +
            "([string]$_.Group -eq 'PenguinPanel Auto') -or " +
            "([string]$_.DisplayName).StartsWith('PenguinPanel ', [System.StringComparison]::OrdinalIgnoreCase) " +
            "}); " +
            "foreach ($rule in $managedRules) { " +
            "$filter = @($rule | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue | Select-Object -First 1)[0]; " +
            "$firewallItems += [pscustomobject]@{ Name=[string]$rule.Name; DisplayName=[string]$rule.DisplayName; Group=[string]$rule.Group; Protocol=if ($null -eq $filter) { '' } else { [string]$filter.Protocol }; LocalPort=if ($null -eq $filter) { '' } else { [string]$filter.LocalPort }; Direction=[string]$rule.Direction; Action=[string]$rule.Action; Enabled=([string]$rule.Enabled -eq 'True') }; " +
            "} " +

            "$storageItems = @(); " +
            "foreach ($root in @($storageRoots)) { " +
            "if ([string]::IsNullOrWhiteSpace([string]$root) -or -not (Test-Path -LiteralPath $root)) { continue }; " +
            "foreach ($disk in @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.vhdx','.vhd' })) { " +
            "$storageItems += [pscustomobject]@{ Root=[string]$root; Path=[string]$disk.FullName; SizeBytes=[int64]$disk.Length }; " +
            "} " +
            "} " +

            "[pscustomobject]@{ NatName=$natName; NatExists=[bool]$natExists; Vms=@($vmItems); NatMappings=@($natItems); ManagedFirewallRules=@($firewallItems); StorageDisks=@($storageItems); CheckedAt=(Get-Date).ToUniversalTime().ToString('o',[System.Globalization.CultureInfo]::InvariantCulture) } | ConvertTo-Json -Compress -Depth 8";

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<ConsistencyHostInventoryJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Consistency host inventory returned no result.");

        return new ConsistencyHostInventory(
            item.NatName ?? natName,
            item.NatExists,
            (item.Vms ?? []).Select(vm => new ConsistencyHostVm(
                vm.Name ?? string.Empty,
                vm.State ?? "Unknown",
                vm.VmPath ?? string.Empty,
                vm.DiskPaths ?? [])).ToList(),
            (item.NatMappings ?? []).Select(map => new ConsistencyNatMapping(
                map.NatName ?? natName,
                NormalizeProtocolName(map.Protocol),
                map.ExternalIpAddress ?? string.Empty,
                map.ExternalPort,
                map.InternalIpAddress ?? string.Empty,
                map.InternalPort)).ToList(),
            (item.ManagedFirewallRules ?? []).Select(rule => new ConsistencyFirewallRule(
                rule.Name ?? string.Empty,
                rule.DisplayName ?? string.Empty,
                rule.Group ?? string.Empty,
                NormalizeProtocolName(rule.Protocol),
                rule.LocalPort ?? string.Empty,
                rule.Direction ?? string.Empty,
                rule.Action ?? string.Empty,
                rule.Enabled)).ToList(),
            (item.StorageDisks ?? []).Select(disk => new ConsistencyStorageDisk(
                disk.Root ?? string.Empty,
                disk.Path ?? string.Empty,
                disk.SizeBytes)).ToList(),
            ParseDateTime(item.CheckedAt));
    }

    public async Task<VmHostInfrastructureHealth> GetVmHostInfrastructureHealthAsync(VmNetworkPortProfile profile)
    {
        var mappings = BuildPortMappings(profile).ToList();
        var configuredNatName = configuration["NetworkProfiles:NatName"] ?? string.Empty;
        var natName = NormalizeNatName(string.IsNullOrWhiteSpace(profile.NatName) ? configuredNatName : profile.NatName);

        var specs = mappings
            .Select(mapping =>
                "[pscustomobject]@{ Label=" + Quote(mapping.Label)
                + "; Protocol=" + Quote(mapping.Protocol.ToUpperInvariant())
                + "; ExternalPort=" + mapping.ExternalPort.ToString(CultureInfo.InvariantCulture)
                + "; InternalPort=" + mapping.InternalPort.ToString(CultureInfo.InvariantCulture)
                + " }")
            .ToList();

        var script =
            "$ErrorActionPreference = 'Stop'; " +
            "$vmName = " + Quote(profile.VmName) + "; " +
            "$natName = " + Quote(natName) + "; " +
            "$expectedInternalIp = " + Quote((profile.InternalIpAddress ?? string.Empty).Trim()) + "; " +
            "$specs = @(" + string.Join(",", specs) + "); " +
            "$vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue; " +
            "$vmExists = ($null -ne $vm); " +
            "$vmState = if ($vmExists) { [string]$vm.State } else { 'Missing' }; " +
            "$diskPath = ''; $diskExists = $false; " +
            "if ($vmExists) { " +
            "$disk = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction SilentlyContinue | Select-Object -First 1)[0]; " +
            "if ($null -ne $disk) { $diskPath = [string]$disk.Path; if (-not [string]::IsNullOrWhiteSpace($diskPath)) { $diskExists = Test-Path -LiteralPath $diskPath } } " +
            "} " +
            "$reportedIps = @(); " +
            "if ($vmExists) { " +
            "$reportedIps = @(Get-VMNetworkAdapter -VMName $vmName -ErrorAction SilentlyContinue | ForEach-Object { @($_.IPAddresses) } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Sort-Object -Unique); " +
            "} " +
            "$internalIpObserved = (-not [string]::IsNullOrWhiteSpace($expectedInternalIp)) -and (@($reportedIps | Where-Object { [string]$_ -eq $expectedInternalIp }).Count -gt 0); " +
            "if ($natName -match '^vEthernet \\((.+)\\)$') { $natName = $Matches[1] } " +
            "$nat = if ([string]::IsNullOrWhiteSpace($natName)) { @(Get-NetNat -ErrorAction SilentlyContinue | Select-Object -First 1)[0] } else { Get-NetNat -Name $natName -ErrorAction SilentlyContinue }; " +
            "$natExists = ($null -ne $nat); " +
            "if ($natExists) { $natName = [string]$nat.Name } " +
            "$portHealth = @(); " +
            "foreach ($spec in @($specs)) { " +
            "$protocol = ([string]$spec.Protocol).ToUpperInvariant(); " +
            "$externalPort = [int]$spec.ExternalPort; " +
            "$internalPort = [int]$spec.InternalPort; " +
            "$natHealthy = $false; $natDetails = ''; " +
            "if ($natExists) { " +
            "$matches = @(Get-NetNatStaticMapping -NatName $natName -ErrorAction SilentlyContinue | Where-Object { " +
            "(([string]$_.Protocol).ToUpperInvariant() -eq $protocol) -and " +
            "([int]$_.ExternalPort -eq $externalPort) -and " +
            "([string]$_.InternalIPAddress -eq $expectedInternalIp) -and " +
            "([int]$_.InternalPort -eq $internalPort) }); " +
            "$natHealthy = ($matches.Count -gt 0); " +
            "$natDetails = if ($natHealthy) { ($protocol + '/' + $externalPort + ' -> ' + $expectedInternalIp + ':' + $internalPort) } else { 'Expected WinNAT mapping is missing or points to a different target.' }; " +
            "} else { $natDetails = 'Configured WinNAT object was not found.' } " +
            "$rulePrefix = '[Penguin VPS] ' + $vmName + ' - ' + [string]$spec.Label + ' ' + $protocol + ' - Public ' + $externalPort + ' -> '; " +
            "$expectedRuleName = $rulePrefix + $expectedInternalIp + ':' + $internalPort; " +
            "$candidateRules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { [string]$_.DisplayName -eq $expectedRuleName }); " +
            "$hostFirewallHealthy = $false; " +
            "foreach ($candidateRule in $candidateRules) { " +
            "if (([string]$candidateRule.Direction -ne 'Inbound') -or ([string]$candidateRule.Action -ne 'Allow') -or ([string]$candidateRule.Enabled -ne 'True')) { continue } " +
            "$filters = @($candidateRule | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue); " +
            "foreach ($filter in $filters) { " +
            "$reportedProtocol = ([string]$filter.Protocol).ToUpperInvariant(); " +
            "$protocolMatches = ($reportedProtocol -eq $protocol); " +
            "if ((-not $protocolMatches) -and $protocol -eq 'TCP') { $protocolMatches = ($reportedProtocol -eq '6') } " +
            "if ((-not $protocolMatches) -and $protocol -eq 'UDP') { $protocolMatches = ($reportedProtocol -eq '17') } " +
            "if ($protocolMatches -and ([string]$filter.LocalPort -eq [string]$externalPort)) { $hostFirewallHealthy = $true; break } " +
            "} " +
            "if ($hostFirewallHealthy) { break } " +
            "} " +
            "$hostFirewallDetails = if ($hostFirewallHealthy) { $expectedRuleName } else { 'Expected host Windows Firewall rule is missing or does not match the public port.' }; " +
            "$portHealth += [pscustomobject]@{ " +
            "Label=[string]$spec.Label; Protocol=$protocol; ExternalPort=$externalPort; InternalPort=$internalPort; " +
            "NatHealthy=[bool]$natHealthy; HostFirewallHealthy=[bool]$hostFirewallHealthy; NatDetails=$natDetails; HostFirewallDetails=$hostFirewallDetails }; " +
            "} " +
            "[pscustomobject]@{ " +
            "VmName=$vmName; VmExists=[bool]$vmExists; VmState=$vmState; DiskPath=$diskPath; DiskExists=[bool]$diskExists; " +
            "NatName=$natName; NatExists=[bool]$natExists; ExpectedInternalIp=$expectedInternalIp; HyperVReportedIps=@($reportedIps); InternalIpObserved=[bool]$internalIpObserved; " +
            "Ports=@($portHealth); CheckedAt=(Get-Date).ToUniversalTime().ToString('o',[System.Globalization.CultureInfo]::InvariantCulture) " +
            "} | ConvertTo-Json -Compress -Depth 8";

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<VmHostInfrastructureHealthJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("VM host health diagnostics returned no result.");

        var ports = (item.Ports ?? [])
            .Select(port => new VmHostPortHealth(
                port.Label ?? "Port",
                port.Protocol ?? "TCP",
                port.ExternalPort,
                port.InternalPort,
                port.NatHealthy,
                port.HostFirewallHealthy,
                port.NatDetails ?? string.Empty,
                port.HostFirewallDetails ?? string.Empty))
            .ToList();

        return new VmHostInfrastructureHealth(
            item.VmName ?? profile.VmName,
            item.VmExists,
            item.VmState ?? "Unknown",
            item.DiskPath ?? string.Empty,
            item.DiskExists,
            item.NatName ?? natName,
            item.NatExists,
            item.ExpectedInternalIp ?? profile.InternalIpAddress ?? string.Empty,
            item.HyperVReportedIps ?? [],
            item.InternalIpObserved,
            ports,
            ParseDateTime(item.CheckedAt));
    }

    public async Task<NetworkProfileApplyResult> ApplyNetworkPortProfileAsync(VmNetworkPortProfile profile)
    {
        var mappings = BuildPortMappings(profile).ToList();
        if (mappings.Count == 0)
        {
            return new NetworkProfileApplyResult(profile.VmName, 0, "No enabled NAT/firewall port rules in this profile.");
        }

        if (string.IsNullOrWhiteSpace(profile.InternalIpAddress))
        {
            throw new InvalidOperationException("Internal IPv4 is required before NAT/port rules can be applied.");
        }

        var configuredNatName = configuration["NetworkProfiles:NatName"] ?? string.Empty;
        var natName = NormalizeNatName(string.IsNullOrWhiteSpace(profile.NatName) ? configuredNatName : profile.NatName);

        // PublicIpAddress is the address customers connect to (for example 81.16.177.72).
        // It is NOT necessarily a valid WinNAT ExternalIPAddress binding. On a normal
        // Hyper-V internal NAT, static inbound mappings should bind to 0.0.0.0 so they
        // listen on the host's external addresses. Keep the two concepts separate.
        var natBindAddress = (configuration["NetworkProfiles:NatBindAddress"] ?? "0.0.0.0").Trim();
        if (string.IsNullOrWhiteSpace(natBindAddress))
        {
            natBindAddress = "0.0.0.0";
        }

        var script =
            "$ErrorActionPreference = 'Stop'; " +
            "$vmName = " + Quote(profile.VmName) + "; " +
            "$natName = " + Quote(natName) + "; " +
            "$internalIp = " + Quote(profile.InternalIpAddress.Trim()) + "; " +
            "$natBindAddress = " + Quote(natBindAddress) + "; " +
            "if ($natName -match '^vEthernet \\((.+)\\)$') { $natName = $Matches[1] } " +
            "if ([string]::IsNullOrWhiteSpace($natName)) { $natName = [string](@(Get-NetNat -ErrorAction Stop | Select-Object -First 1).Name) } " +
            "if ([string]::IsNullOrWhiteSpace($natName)) { throw 'No WinNAT network was found. Fill NAT name in the port profile or appsettings NetworkProfiles:NatName.' } " +
            "$nat = Get-NetNat -Name $natName -ErrorAction SilentlyContinue; " +
            "if ($null -eq $nat) { $available = (@(Get-NetNat -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name) -join ', '); throw (\"WinNAT '\" + $natName + \"' was not found. Use the NAT object name from Get-NetNat, not the vEthernet adapter/switch name. Available NATs: \" + $available) } " +
            "function Add-PenguinMapping($protocol, $externalPort, $internalPort, $label) { " +
            "$protocol = ([string]$protocol).ToUpperInvariant(); " +
            "$existing = @(Get-NetNatStaticMapping -NatName $natName -ErrorAction SilentlyContinue | Where-Object { ([string]$_.Protocol).ToUpperInvariant() -eq $protocol -and [int]$_.ExternalPort -eq [int]$externalPort }); " +
            "$foreign = @($existing | Where-Object { ([string]$_.InternalIPAddress -ne $internalIp) }); " +
            "if ($foreign.Count -gt 0) { $targets = ($foreign | ForEach-Object { ([string]$_.InternalIPAddress + ':' + [int]$_.InternalPort) } | Sort-Object -Unique) -join ', '; throw ('Cannot map ' + $protocol + '/' + $externalPort + ' for ' + $vmName + '. That external port is already mapped to another VM target: ' + $targets + '.') } " +
            "$sameVmMappings = @($existing | Where-Object { ([string]$_.InternalIPAddress -eq $internalIp) }); " +
            "$sameTarget = @($sameVmMappings | Where-Object { ([int]$_.InternalPort -eq [int]$internalPort) }); " +
            "$correctBinding = @($sameTarget | Where-Object { ([string]$_.ExternalIPAddress -eq $natBindAddress) }); " +
            "if ($correctBinding.Count -eq 0) { " +
            "foreach ($map in $sameVmMappings) { Remove-NetNatStaticMapping -StaticMappingID $map.StaticMappingID -Confirm:$false -ErrorAction Stop } " +
            // IMPORTANT: do not emit a PowerShell '#' line comment here. The generated
            // script is sent as one logical line; '#' would comment out every command and
            // closing brace that follows it, causing MissingEndCurlyBrace before NAT runs.
            "Add-NetNatStaticMapping -NatName $natName -Protocol $protocol -ExternalIPAddress $natBindAddress -ExternalPort ([int]$externalPort) -InternalIPAddress $internalIp -InternalPort ([int]$internalPort) -ErrorAction Stop | Out-Null; " +
            "} " +
            "$verified = @(Get-NetNatStaticMapping -NatName $natName -ErrorAction SilentlyContinue | Where-Object { ([string]$_.Protocol).ToUpperInvariant() -eq $protocol -and [int]$_.ExternalPort -eq [int]$externalPort -and ([string]$_.InternalIPAddress -eq $internalIp) -and [int]$_.InternalPort -eq [int]$internalPort }); " +
            "if ($verified.Count -eq 0) { throw ('WinNAT did not retain the requested ' + $protocol + '/' + $externalPort + ' mapping for ' + $vmName + '.') } " +
            "$legacyRuleName = ('PenguinPanel ' + $vmName + ' ' + $label + ' ' + $protocol + ' ' + $externalPort); " +
            "$rulePrefix = ('[Penguin VPS] ' + $vmName + ' - ' + $label + ' ' + $protocol + ' - Public ' + $externalPort + ' -> '); " +
            "$ruleName = ($rulePrefix + $internalIp + ':' + $internalPort); " +
            "$firewallVerified = $false; " +
            "for ($attempt = 1; $attempt -le 2 -and -not $firewallVerified; $attempt++) { " +
            "@(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { ([string]$_.DisplayName -eq $legacyRuleName) -or ([string]$_.DisplayName).StartsWith($rulePrefix, [System.StringComparison]::OrdinalIgnoreCase) }) | Remove-NetFirewallRule -ErrorAction SilentlyContinue; " +
            "New-NetFirewallRule -DisplayName $ruleName -Group 'PenguinPanel VPS' -Direction Inbound -Protocol $protocol -LocalPort ([int]$externalPort) -RemotePort Any -Action Allow -Profile Any -Enabled True -ErrorAction Stop | Out-Null; " +
            "$candidateRules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -ceq $ruleName }); " +
            "foreach ($candidateRule in $candidateRules) { " +
            "$ruleIsInbound = ([string]$candidateRule.Direction -eq 'Inbound'); " +
            "$ruleIsAllow = ([string]$candidateRule.Action -eq 'Allow'); " +
            "$ruleIsEnabled = ([string]$candidateRule.Enabled -eq 'True'); " +
            "if (-not ($ruleIsInbound -and $ruleIsAllow -and $ruleIsEnabled)) { continue } " +
            "$portFilters = @($candidateRule | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue); " +
            "foreach ($portFilter in $portFilters) { " +
            "$reportedProtocol = ([string]$portFilter.Protocol).ToUpperInvariant(); " +
            "$reportedLocalPort = [string]$portFilter.LocalPort; " +
            "$expectedLocalPort = [string]$externalPort; " +
            "$protocolMatches = ($reportedProtocol -eq $protocol); " +
            "if ((-not $protocolMatches) -and ($protocol -eq 'TCP')) { $protocolMatches = ($reportedProtocol -eq '6') } " +
            "if ((-not $protocolMatches) -and ($protocol -eq 'UDP')) { $protocolMatches = ($reportedProtocol -eq '17') } " +
            "$portMatches = ($reportedLocalPort -eq $expectedLocalPort); " +
            "if ($protocolMatches -and $portMatches) { $firewallVerified = $true; break } " +
            "} " +
            "if ($firewallVerified) { break } " +
            "} " +
            "if (-not $firewallVerified -and $attempt -lt 2) { Start-Sleep -Milliseconds 750 } " +
            "} " +
            "if (-not $firewallVerified) { throw ('Windows Firewall did not retain the requested host rule ' + $ruleName + '. NAT mapping exists but the public port is not safely opened on the Hyper-V host.') } " +
            "}; ";

        foreach (var mapping in mappings)
        {
            script += "Add-PenguinMapping " + Quote(mapping.Protocol) + " " + mapping.ExternalPort.ToString(CultureInfo.InvariantCulture) + " " + mapping.InternalPort.ToString(CultureInfo.InvariantCulture) + " " + Quote(mapping.Label) + "; ";
        }

        script += "[pscustomobject]@{ VmName=$vmName; RulesApplied=" + mappings.Count.ToString(CultureInfo.InvariantCulture) + "; Message=('Applied ' + " + mappings.Count.ToString(CultureInfo.InvariantCulture) + " + ' NAT/firewall rule(s) using NAT ' + $natName + ', WinNAT bind ' + $natBindAddress + ', to ' + $internalIp + '.') } | ConvertTo-Json -Compress -Depth 4";

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<NetworkProfileApplyJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Network profile apply returned no result.");

        return new NetworkProfileApplyResult(
            result.VmName ?? profile.VmName,
            result.RulesApplied,
            result.Message ?? $"Applied {mappings.Count} NAT/firewall rule(s).");
    }


    public async Task<int> RemoveExactNatMappingAsync(
        string natName,
        string protocol,
        int externalPort,
        string internalIpAddress,
        int internalPort)
    {
        natName = NormalizeNatName(natName);
        protocol = NormalizeProtocolName(protocol);
        internalIpAddress = (internalIpAddress ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(natName)
            || (protocol is not "TCP" and not "UDP")
            || externalPort is < 1 or > 65535
            || internalPort is < 1 or > 65535
            || string.IsNullOrWhiteSpace(internalIpAddress))
        {
            throw new InvalidOperationException("Exact NAT cleanup target is incomplete or invalid.");
        }

        var script =
            "$ErrorActionPreference = 'Stop'; " +
            "$natName = " + Quote(natName) + "; " +
            "$protocol = " + Quote(protocol) + "; " +
            "$externalPort = " + externalPort.ToString(CultureInfo.InvariantCulture) + "; " +
            "$internalIp = " + Quote(internalIpAddress) + "; " +
            "$internalPort = " + internalPort.ToString(CultureInfo.InvariantCulture) + "; " +
            "$nat = Get-NetNat -Name $natName -ErrorAction SilentlyContinue; " +
            "if ($null -eq $nat) { [pscustomobject]@{ Removed=0 } | ConvertTo-Json -Compress; exit 0 } " +
            "$matches = @(Get-NetNatStaticMapping -NatName $natName -ErrorAction SilentlyContinue | Where-Object { " +
            "$p = ([string]$_.Protocol).ToUpperInvariant(); " +
            "(($p -eq $protocol) -or ($protocol -eq 'TCP' -and $p -eq '6') -or ($protocol -eq 'UDP' -and $p -eq '17')) -and " +
            "([int]$_.ExternalPort -eq $externalPort) -and " +
            "([string]$_.InternalIPAddress -eq $internalIp) -and " +
            "([int]$_.InternalPort -eq $internalPort) }); " +
            "$removed = 0; " +
            "foreach ($map in $matches) { Remove-NetNatStaticMapping -StaticMappingID $map.StaticMappingID -Confirm:$false -ErrorAction Stop; $removed++ } " +
            "$remaining = @(Get-NetNatStaticMapping -NatName $natName -ErrorAction SilentlyContinue | Where-Object { " +
            "$p = ([string]$_.Protocol).ToUpperInvariant(); " +
            "(($p -eq $protocol) -or ($protocol -eq 'TCP' -and $p -eq '6') -or ($protocol -eq 'UDP' -and $p -eq '17')) -and " +
            "([int]$_.ExternalPort -eq $externalPort) -and " +
            "([string]$_.InternalIPAddress -eq $internalIp) -and " +
            "([int]$_.InternalPort -eq $internalPort) }); " +
            "if ($remaining.Count -gt 0) { throw 'Exact WinNAT mapping is still present after cleanup.' } " +
            "[pscustomobject]@{ Removed=$removed } | ConvertTo-Json -Compress";

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<CleanupCountJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Exact WinNAT cleanup returned no result.");
        return result.Removed;
    }

    public async Task<int> RemoveManagedHostFirewallRuleAsync(
        string ruleName,
        string expectedDisplayName)
    {
        ruleName = (ruleName ?? string.Empty).Trim();
        expectedDisplayName = (expectedDisplayName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(ruleName))
        {
            throw new InvalidOperationException("Managed firewall cleanup requires the exact firewall rule name.");
        }

        var script =
            "$ErrorActionPreference = 'Stop'; " +
            "$ruleName = " + Quote(ruleName) + "; " +
            "$expectedDisplayName = " + Quote(expectedDisplayName) + "; " +
            "$rule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue; " +
            "if ($null -eq $rule) { [pscustomobject]@{ Removed=0 } | ConvertTo-Json -Compress; exit 0 } " +
            "$display = [string]$rule.DisplayName; $group = [string]$rule.Group; " +
            "$managed = $display.StartsWith('[Penguin VPS] ', [System.StringComparison]::OrdinalIgnoreCase) -or " +
            "$display.StartsWith('PenguinPanel ', [System.StringComparison]::OrdinalIgnoreCase) -or " +
            "($group -eq 'PenguinPanel Auto'); " +
            "if (-not $managed) { throw ('Firewall rule is no longer PenguinPanel-managed: ' + $display) } " +
            "if (-not [string]::IsNullOrWhiteSpace($expectedDisplayName) -and $display -ne $expectedDisplayName) { " +
            "throw ('Firewall rule changed since preview. Expected display name: ' + $expectedDisplayName + '; current: ' + $display) } " +
            "Remove-NetFirewallRule -Name $ruleName -ErrorAction Stop; " +
            "$remaining = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue; " +
            "if ($null -ne $remaining) { throw 'Managed firewall rule is still present after cleanup.' } " +
            "[pscustomobject]@{ Removed=1 } | ConvertTo-Json -Compress";

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<CleanupCountJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Managed firewall cleanup returned no result.");
        return result.Removed;
    }

    public async Task<NetworkProfileCleanupResult> RemoveNetworkPortProfileAsync(VmNetworkPortProfile profile)
    {
        var mappings = BuildPortMappings(profile).ToList();
        var configuredNatName = configuration["NetworkProfiles:NatName"] ?? string.Empty;
        var natName = NormalizeNatName(string.IsNullOrWhiteSpace(profile.NatName) ? configuredNatName : profile.NatName);
        var internalIp = (profile.InternalIpAddress ?? string.Empty).Trim();

        var script =
            "$ErrorActionPreference = 'Stop'; " +
            "$vmName = " + Quote(profile.VmName) + "; " +
            "$natName = " + Quote(natName) + "; " +
            "$internalIp = " + Quote(internalIp) + "; " +
            "if ($natName -match '^vEthernet \\((.+)\\)$') { $natName = $Matches[1] } " +
            "$specs = @(); ";

        foreach (var mapping in mappings)
        {
            script += "$specs += [pscustomobject]@{ Protocol=" + Quote(mapping.Protocol) + "; ExternalPort=" + mapping.ExternalPort.ToString(CultureInfo.InvariantCulture) + " }; ";
        }

        script +=
            "$removedMappings = 0; $removedFirewallRules = 0; " +
            "$expectsNetworkCleanup = (-not [string]::IsNullOrWhiteSpace($internalIp)) -or @($specs).Count -gt 0; " +
            "$nat = if ([string]::IsNullOrWhiteSpace($natName)) { @(Get-NetNat -ErrorAction SilentlyContinue | Select-Object -First 1)[0] } else { Get-NetNat -Name $natName -ErrorAction SilentlyContinue }; " +
            "if ($expectsNetworkCleanup -and $null -eq $nat) { throw (\"WinNAT '\" + $natName + \"' could not be found, so PenguinPanel cannot prove the VPS mappings were removed.\") } " +
            "if ($null -ne $nat) { " +
            "$natName = [string]$nat.Name; " +
            "$allMappings = @(Get-NetNatStaticMapping -NatName $natName -ErrorAction SilentlyContinue); " +
            "foreach ($map in $allMappings) { " +
            "$mapProtocol = ([string]$map.Protocol).ToUpperInvariant(); $mapPort = [int]$map.ExternalPort; " +
            "$matchesIp = (-not [string]::IsNullOrWhiteSpace($internalIp)) -and ([string]$map.InternalIPAddress -eq $internalIp); " +
            "$matchesSpec = @($specs | Where-Object { ([string]$_.Protocol).ToUpperInvariant() -eq $mapProtocol -and [int]$_.ExternalPort -eq $mapPort }).Count -gt 0; " +
            "if ($matchesIp -or $matchesSpec) { Remove-NetNatStaticMapping -StaticMappingID $map.StaticMappingID -Confirm:$false -ErrorAction Stop; $removedMappings++ } " +
            "} " +
            "$remainingMappings = @(Get-NetNatStaticMapping -NatName $natName -ErrorAction SilentlyContinue | Where-Object { $mapProtocol = ([string]$_.Protocol).ToUpperInvariant(); $mapPort = [int]$_.ExternalPort; ((-not [string]::IsNullOrWhiteSpace($internalIp)) -and ([string]$_.InternalIPAddress -eq $internalIp)) -or (@($specs | Where-Object { ([string]$_.Protocol).ToUpperInvariant() -eq $mapProtocol -and [int]$_.ExternalPort -eq $mapPort }).Count -gt 0) }); " +
            "if ($remainingMappings.Count -gt 0) { throw (\"One or more WinNAT mappings for '$vmName' are still present after cleanup. The VPS will not be deleted.\") } " +
            "} " +
            "$rulePrefix = '[Penguin VPS] ' + $vmName + ' - '; " +
            "$legacyRulePrefix = 'PenguinPanel ' + $vmName + ' '; " +
            "$rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { ([string]$_.DisplayName).StartsWith($rulePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or ([string]$_.DisplayName).StartsWith($legacyRulePrefix, [System.StringComparison]::OrdinalIgnoreCase) }); " +
            "foreach ($rule in $rules) { Remove-NetFirewallRule -Name $rule.Name -ErrorAction Stop; $removedFirewallRules++ } " +
            "$remainingRules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { ([string]$_.DisplayName).StartsWith($rulePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or ([string]$_.DisplayName).StartsWith($legacyRulePrefix, [System.StringComparison]::OrdinalIgnoreCase) }); " +
            "if ($remainingRules.Count -gt 0) { throw (\"One or more root Windows Firewall rules for '$vmName' are still present after cleanup. The VPS will not be deleted.\") } " +
            "[pscustomobject]@{ VmName=$vmName; NatMappingsRemoved=$removedMappings; HostFirewallRulesRemoved=$removedFirewallRules; Message=('Removed ' + $removedMappings + ' WinNAT mapping(s) and ' + $removedFirewallRules + ' host firewall rule(s) for ' + $vmName + '.') } | ConvertTo-Json -Compress -Depth 4";

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<NetworkProfileCleanupJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Network cleanup returned no result.");

        return new NetworkProfileCleanupResult(
            result.VmName ?? profile.VmName,
            result.NatMappingsRemoved,
            result.HostFirewallRulesRemoved,
            result.Message ?? "Network cleanup completed.");
    }

    public async Task<VmDeleteResult> DeleteVmAndStorageAsync(string vmName, IReadOnlyList<string> allowedStorageRoots)
    {
        var roots = allowedStorageRoots
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roots.Count == 0)
        {
            throw new InvalidOperationException("No managed VM storage roots are configured. Refusing to delete VM files.");
        }

        var script =
            "$ErrorActionPreference = 'Stop'; " +
            "$vmName = " + Quote(vmName) + "; " +
            "$allowedRoots = @(" + string.Join(",", roots.Select(Quote)) + "); " +
            "$vm = Get-VM -Name $vmName -ErrorAction Stop; " +
            "$vmPath = [System.IO.Path]::GetFullPath([string]$vm.Path).TrimEnd('\\'); " +
            "$normalizedRoots = @($allowedRoots | ForEach-Object { [System.IO.Path]::GetFullPath([string]$_).TrimEnd('\\') }); " +
            "$matchedRoot = @($normalizedRoots | Where-Object { $vmPath.Equals($_, [System.StringComparison]::OrdinalIgnoreCase) -or $vmPath.StartsWith($_ + '\\', [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)[0]; " +
            "if ([string]::IsNullOrWhiteSpace([string]$matchedRoot)) { throw (\"VM path '" + "$vmPath" + "' is outside PenguinPanel managed storage roots. Refusing to remove files. Allowed roots: \" + ($normalizedRoots -join ', ')) } " +
            "$candidate = $vmPath; $ownedFolder = $null; " +
            "while (-not [string]::IsNullOrWhiteSpace($candidate) -and ($candidate.StartsWith([string]$matchedRoot, [System.StringComparison]::OrdinalIgnoreCase))) { " +
            "if ([string](Split-Path -Leaf $candidate) -eq $vmName) { $ownedFolder = $candidate; break }; " +
            "$parent = Split-Path -Parent $candidate; if ($parent -eq $candidate) { break }; $candidate = $parent; " +
            "} " +
            "if ([string]::IsNullOrWhiteSpace([string]$ownedFolder)) { " +
            "$diskPaths = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Path); " +
            "foreach ($diskPath in $diskPaths) { if ([string]::IsNullOrWhiteSpace([string]$diskPath)) { continue }; $candidate = Split-Path -Parent ([System.IO.Path]::GetFullPath([string]$diskPath)); while (-not [string]::IsNullOrWhiteSpace($candidate) -and $candidate.StartsWith([string]$matchedRoot, [System.StringComparison]::OrdinalIgnoreCase)) { if ([string](Split-Path -Leaf $candidate) -eq $vmName) { $ownedFolder = $candidate; break }; $parent = Split-Path -Parent $candidate; if ($parent -eq $candidate) { break }; $candidate = $parent }; if ($ownedFolder) { break } } " +
            "} " +
            "if ([string]::IsNullOrWhiteSpace([string]$ownedFolder)) { throw (\"Could not identify the dedicated PenguinPanel folder for VM '$vmName'. The VM was NOT deleted.\") } " +
            "$ownedFolder = [System.IO.Path]::GetFullPath([string]$ownedFolder).TrimEnd('\\'); " +
            "if (-not ($ownedFolder.StartsWith([string]$matchedRoot + '\\', [System.StringComparison]::OrdinalIgnoreCase))) { throw 'Safety check failed for VM storage folder.' } " +
            "if ([string]$vm.State -ne 'Off') { Stop-VM -Name $vmName -TurnOff -Force -Confirm:$false -ErrorAction Stop | Out-Null } " +
            "Remove-VM -Name $vmName -Force -Confirm:$false -ErrorAction Stop; " +
            "$storageDeleted = $false; " +
            "if (Test-Path -LiteralPath $ownedFolder) { Remove-Item -LiteralPath $ownedFolder -Recurse -Force -ErrorAction Stop; $storageDeleted = -not (Test-Path -LiteralPath $ownedFolder) } else { $storageDeleted = $true } " +
            "[pscustomobject]@{ VmName=$vmName; VmPath=$ownedFolder; NatMappingsRemoved=0; HostFirewallRulesRemoved=0; StorageDeleted=[bool]$storageDeleted; Message=(\"Deleted VM '$vmName' and its managed storage folder '$ownedFolder'.\") } | ConvertTo-Json -Compress -Depth 4";

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<VmDeleteJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("VM deletion returned no result.");

        return new VmDeleteResult(
            result.VmName ?? vmName,
            result.VmPath ?? string.Empty,
            0,
            0,
            result.StorageDeleted,
            result.Message ?? "VM deleted.");
    }


    public async Task<IReadOnlyList<VmGuestFirewallRule>> GetWindowsGuestFirewallRulesAsync(string vmName, string username, string password)
    {
        ValidateGuestCredentials(username, password);

        var script = $$"""
            $ErrorActionPreference = 'Stop';
            $vmName = {{Quote(vmName)}};
            $username = {{Quote(username)}};
            $plainPassword = {{Quote(password)}};
            $securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force;
            $credential = [pscredential]::new($username, $securePassword);

            $guestScript = {
                $ErrorActionPreference = 'Stop';
                $rules = @(
                    Get-NetFirewallRule -ErrorAction SilentlyContinue |
                    Where-Object { [string]$_.Group -in @('PenguinPanel Auto', 'PenguinPanel Custom') }
                );

                $items = foreach ($rule in $rules) {
                    $portFilter = @(Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule -ErrorAction SilentlyContinue | Select-Object -First 1)[0];
                    $addressFilter = @(Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $rule -ErrorAction SilentlyContinue | Select-Object -First 1)[0];
                    $protocol = if ($null -ne $portFilter) { [string]$portFilter.Protocol } else { 'Any' };
                    if ($protocol -eq '6') { $protocol = 'TCP' } elseif ($protocol -eq '17') { $protocol = 'UDP' }
                    $localPort = if ($null -ne $portFilter) { [string]$portFilter.LocalPort } else { 'Any' };
                    $remoteAddress = if ($null -ne $addressFilter) { (@($addressFilter.RemoteAddress) -join ', ') } else { 'Any' };

                    [pscustomobject]@{
                        Name = [string]$rule.Name;
                        DisplayName = [string]$rule.DisplayName;
                        Group = [string]$rule.Group;
                        Protocol = $protocol;
                        LocalPort = $localPort;
                        RemoteAddress = $remoteAddress;
                        Action = [string]$rule.Action;
                        Enabled = ([string]$rule.Enabled -eq 'True');
                        IsAutomatic = ([string]$rule.Group -eq 'PenguinPanel Auto');
                    };
                }

                if (@($items).Count -eq 0) { '[]' } else { @($items) | Sort-Object DisplayName | ConvertTo-Json -Compress -Depth 5 }
            };

            Invoke-Command -VMName $vmName -Credential $credential -ScriptBlock $guestScript -ErrorAction Stop;
            """;

        var output = await RunJsonAsync(script);
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var items = output.TrimStart().StartsWith("[", StringComparison.Ordinal)
            ? JsonSerializer.Deserialize<List<GuestFirewallRuleJson>>(output, JsonOptions) ?? []
            : [JsonSerializer.Deserialize<GuestFirewallRuleJson>(output, JsonOptions)!];

        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new VmGuestFirewallRule(
                item.Name ?? string.Empty,
                item.DisplayName ?? item.Name ?? string.Empty,
                item.Group ?? string.Empty,
                item.Protocol ?? "Any",
                item.LocalPort ?? "Any",
                item.RemoteAddress ?? "Any",
                item.Action ?? "Allow",
                item.Enabled,
                item.IsAutomatic))
            .ToList();
    }

    public async Task<VmGuestFirewallRule> AddWindowsGuestFirewallRuleAsync(
        string vmName,
        string username,
        string password,
        string displayName,
        string protocol,
        int localPort,
        string remoteAddress,
        string action)
    {
        ValidateGuestCredentials(username, password);
        protocol = NormalizeFirewallProtocol(protocol);
        action = NormalizeFirewallAction(action);
        if (!IsValidPort(localPort))
        {
            throw new InvalidOperationException("Windows Firewall port must be between 1 and 65535.");
        }

        displayName = (displayName ?? string.Empty).Trim();
        if (displayName.Length is < 2 or > 80)
        {
            throw new InvalidOperationException("Firewall rule name must be between 2 and 80 characters.");
        }

        remoteAddress = string.IsNullOrWhiteSpace(remoteAddress) ? "Any" : remoteAddress.Trim();
        if (remoteAddress.Length > 250)
        {
            throw new InvalidOperationException("Remote address value is too long.");
        }

        var script = $$"""
            $ErrorActionPreference = 'Stop';
            $vmName = {{Quote(vmName)}};
            $username = {{Quote(username)}};
            $plainPassword = {{Quote(password)}};
            $displayName = {{Quote(displayName)}};
            $protocol = {{Quote(protocol)}};
            $localPort = {{localPort.ToString(CultureInfo.InvariantCulture)}};
            $remoteAddress = {{Quote(remoteAddress)}};
            $action = {{Quote(action)}};
            $securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force;
            $credential = [pscredential]::new($username, $securePassword);

            $guestScript = {
                param($ruleDisplayName, $ruleProtocol, $rulePort, $ruleRemoteAddress, $ruleAction)
                $ErrorActionPreference = 'Stop';
                $ruleName = 'PenguinPanel-Custom-' + [guid]::NewGuid().ToString('N');
                $params = @{
                    Name = $ruleName;
                    DisplayName = ('PenguinPanel - ' + $ruleDisplayName);
                    Group = 'PenguinPanel Custom';
                    Direction = 'Inbound';
                    Action = $ruleAction;
                    Enabled = 'True';
                    Profile = 'Any';
                    Protocol = $ruleProtocol;
                    LocalPort = [int]$rulePort;
                };
                if (-not [string]::IsNullOrWhiteSpace($ruleRemoteAddress) -and $ruleRemoteAddress -ne 'Any') {
                    $params.RemoteAddress = $ruleRemoteAddress;
                }
                New-NetFirewallRule @params | Out-Null;
                $rule = Get-NetFirewallRule -Name $ruleName -ErrorAction Stop;
                $portFilter = @(Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule -ErrorAction SilentlyContinue | Select-Object -First 1)[0];
                $addressFilter = @(Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $rule -ErrorAction SilentlyContinue | Select-Object -First 1)[0];
                [pscustomobject]@{
                    Name = [string]$rule.Name;
                    DisplayName = [string]$rule.DisplayName;
                    Group = [string]$rule.Group;
                    Protocol = $ruleProtocol;
                    LocalPort = if ($null -ne $portFilter) { [string]$portFilter.LocalPort } else { [string]$rulePort };
                    RemoteAddress = if ($null -ne $addressFilter) { (@($addressFilter.RemoteAddress) -join ', ') } else { 'Any' };
                    Action = [string]$rule.Action;
                    Enabled = ([string]$rule.Enabled -eq 'True');
                    IsAutomatic = $false;
                } | ConvertTo-Json -Compress -Depth 5;
            };

            Invoke-Command -VMName $vmName -Credential $credential -ScriptBlock $guestScript -ArgumentList $displayName, $protocol, $localPort, $remoteAddress, $action -ErrorAction Stop;
            """;

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<GuestFirewallRuleJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Windows Firewall rule creation returned no result.");

        return new VmGuestFirewallRule(
            item.Name ?? string.Empty,
            item.DisplayName ?? displayName,
            item.Group ?? "PenguinPanel Custom",
            item.Protocol ?? protocol,
            item.LocalPort ?? localPort.ToString(CultureInfo.InvariantCulture),
            item.RemoteAddress ?? remoteAddress,
            item.Action ?? action,
            item.Enabled,
            item.IsAutomatic);
    }

    public Task RemoveWindowsGuestFirewallRuleAsync(string vmName, string username, string password, string ruleName)
    {
        ValidateGuestCredentials(username, password);
        if (string.IsNullOrWhiteSpace(ruleName))
        {
            throw new InvalidOperationException("Select a Windows Firewall rule to remove.");
        }

        var script = $$"""
            $ErrorActionPreference = 'Stop';
            $vmName = {{Quote(vmName)}};
            $username = {{Quote(username)}};
            $plainPassword = {{Quote(password)}};
            $ruleName = {{Quote(ruleName.Trim())}};
            $securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force;
            $credential = [pscredential]::new($username, $securePassword);

            $guestScript = {
                param($targetRuleName)
                $ErrorActionPreference = 'Stop';
                $rule = Get-NetFirewallRule -Name $targetRuleName -ErrorAction Stop;
                if ([string]$rule.Group -eq 'PenguinPanel Auto') {
                    throw 'Automatic PenguinPanel firewall rules cannot be deleted individually. Change the Network profile and use Sync from network profile instead.';
                }
                if ([string]$rule.Group -ne 'PenguinPanel Custom') {
                    throw 'PenguinPanel only removes firewall rules that were created by PenguinPanel.';
                }
                Remove-NetFirewallRule -Name $targetRuleName -ErrorAction Stop;
            };

            Invoke-Command -VMName $vmName -Credential $credential -ScriptBlock $guestScript -ArgumentList $ruleName -ErrorAction Stop | Out-Null;
            """;
        return RunAsync(script);
    }

    public async Task<GuestFirewallApplyResult> SyncWindowsGuestFirewallRulesAsync(VmNetworkPortProfile profile, string username, string password)
    {
        ValidateGuestCredentials(username, password);
        var mappings = BuildPortMappings(profile)
            .GroupBy(item => (item.Protocol.ToUpperInvariant(), item.InternalPort))
            .Select(group => group.First())
            .ToList();

        var script =
            "$vmName = " + Quote(profile.VmName) + "; " +
            "$username = " + Quote(username) + "; " +
            "$plainPassword = " + Quote(password) + "; " +
            "$securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force; " +
            "$credential = [pscredential]::new($username, $securePassword); " +
            "$guestScript = { param($ruleSpecs) $ErrorActionPreference = 'Stop'; " +
            "$canonicalVmName = [string](@($ruleSpecs)[0].VmName); " +
            "if (-not [string]::IsNullOrWhiteSpace($canonicalVmName)) { " +
            "New-Item -Path 'HKLM:\\SOFTWARE\\PenguinPanel' -Force | Out-Null; " +
            "Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\PenguinPanel' -Name 'VmName' -Type String -Value $canonicalVmName -Force; " +
            "} " +
            "$legacyNames = @('PenguinHosting RDP TCP','PenguinHosting RDP UDP','PenguinHosting FiveM TCP','PenguinHosting FiveM UDP'); " +
            "@(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { ([string]$_.Group -eq 'PenguinPanel Auto') -or ([string]$_.DisplayName).StartsWith('[Penguin VPS] ', [System.StringComparison]::OrdinalIgnoreCase) -or ($legacyNames -contains [string]$_.DisplayName) }) | Remove-NetFirewallRule -ErrorAction SilentlyContinue; " +
            "$createdNames = @(); $count = 0; foreach ($spec in @($ruleSpecs)) { " +
            "$safeLabel = ([string]$spec.Label -replace '[^A-Za-z0-9 _.-]', '-').Trim(); if ([string]::IsNullOrWhiteSpace($safeLabel)) { $safeLabel = 'Port' }; " +
            "$ruleName = 'PenguinPanel-Auto-' + [guid]::NewGuid().ToString('N'); " +
            "$displayName = '[Penguin VPS] ' + [string]$spec.VmName + ' - ' + $safeLabel + ' ' + [string]$spec.Protocol + ' - Local ' + [string]$spec.Port; " +
            "New-NetFirewallRule -Name $ruleName -DisplayName $displayName -Group 'PenguinPanel Auto' -Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol ([string]$spec.Protocol) -LocalPort ([int]$spec.Port) -ErrorAction Stop | Out-Null; " +
            "$createdNames += $displayName; $count++ } " +
            "$verified = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $createdNames -contains [string]$_.DisplayName }); " +
            "if ($verified.Count -ne $count) { throw ('Windows Firewall verification failed. Expected ' + $count + ' PenguinPanel rule(s), found ' + $verified.Count + '.') } " +
            "[pscustomobject]@{ RulesApplied=$count; CanonicalVmName=$canonicalVmName } }; ";

        var ruleSpecs = mappings
            .Select(mapping => $"[pscustomobject]@{{ VmName={Quote(profile.VmName)}; Protocol={Quote(mapping.Protocol)}; Port={mapping.InternalPort.ToString(CultureInfo.InvariantCulture)}; Label={Quote(mapping.Label)} }}")
            .ToList();
        script += "$ruleSpecs = @(" + string.Join(",", ruleSpecs) + "); ";
        script += "$result = Invoke-Command -VMName $vmName -Credential $credential -ScriptBlock $guestScript -ArgumentList (,$ruleSpecs) -ErrorAction Stop; ";
        script += "[pscustomobject]@{ VmName=$vmName; RulesApplied=[int]$result.RulesApplied; Message=('Synced ' + [int]$result.RulesApplied + ' Windows Firewall inbound rule(s) inside the VM from the saved PenguinPanel network profile.') } | ConvertTo-Json -Compress -Depth 5";

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<GuestFirewallApplyJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Windows Firewall sync returned no result.");

        return new GuestFirewallApplyResult(
            result.VmName ?? profile.VmName,
            result.RulesApplied,
            result.Message ?? $"Synced {mappings.Count} Windows Firewall rule(s).");
    }


    public async Task<VmGuestDnsStatus> GetWindowsGuestDnsStatusAsync(
        string vmName,
        string username,
        string password,
        string interfaceAlias)
    {
        ValidateGuestCredentials(username, password);

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = {{Quote(vmName)}}
            $username = {{Quote(username)}}
            $plainPassword = {{Quote(password)}}
            $interfaceAlias = {{Quote(interfaceAlias ?? string.Empty)}}

            $securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force
            $credential = [pscredential]::new($username, $securePassword)

            $guestScript = {
                param($requestedInterfaceAlias)
                $ErrorActionPreference = 'Stop'

                if (-not [string]::IsNullOrWhiteSpace($requestedInterfaceAlias)) {
                    $adapters = @(Get-NetAdapter -Name $requestedInterfaceAlias -ErrorAction Stop)
                }
                else {
                    $adapters = @(
                        Get-NetAdapter -ErrorAction Stop |
                        Where-Object {
                            [string]$_.Status -eq 'Up' -and
                            [string]$_.HardwareInterface -ne 'False'
                        } |
                        Sort-Object ifIndex
                    )
                }

                if (@($adapters).Count -eq 0) {
                    throw 'No active network adapter was found inside the VM.'
                }

                $adapter = @($adapters)[0]
                $dns = Get-DnsClientServerAddress `
                    -InterfaceIndex ([int]$adapter.ifIndex) `
                    -AddressFamily IPv4 `
                    -ErrorAction Stop

                [pscustomobject]@{
                    InterfaceAlias = [string]$adapter.Name
                    DnsServers = @(
                        @($dns.ServerAddresses) |
                        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
                    )
                }
            }

            $guest = Invoke-Command `
                -VMName $vmName `
                -Credential $credential `
                -ScriptBlock $guestScript `
                -ArgumentList $interfaceAlias `
                -ErrorAction Stop

            [pscustomobject]@{
                VmName = $vmName
                InterfaceAlias = [string]$guest.InterfaceAlias
                DnsServers = @($guest.DnsServers)
                CheckedAt = (Get-Date).ToUniversalTime().ToString('o',[System.Globalization.CultureInfo]::InvariantCulture)
            } | ConvertTo-Json -Compress -Depth 5
            """;

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<VmGuestDnsStatusJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Windows DNS inspection returned no result.");

        return new VmGuestDnsStatus(
            result.VmName ?? vmName,
            result.InterfaceAlias ?? string.Empty,
            result.DnsServers ?? [],
            ParseDateTime(result.CheckedAt));
    }

    public async Task<VmGuestDnsStatus> SyncWindowsGuestDnsServersAsync(
        VmNetworkPortProfile profile,
        string username,
        string password)
    {
        ValidateGuestCredentials(username, password);

        var expectedServers = new[] {
                (profile.PreferredDnsServer ?? string.Empty).Trim(),
                (profile.AlternateDnsServer ?? string.Empty).Trim()
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (expectedServers.Count == 0)
        {
            throw new InvalidOperationException(
                "No preferred or alternate DNS server is configured in the saved PenguinPanel network profile.");
        }

        var serverArray = "@(" + string.Join(",", expectedServers.Select(Quote)) + ")";

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = {{Quote(profile.VmName)}}
            $username = {{Quote(username)}}
            $plainPassword = {{Quote(password)}}
            $interfaceAlias = {{Quote(profile.DnsInterfaceAlias ?? string.Empty)}}
            $expectedDnsServers = {{serverArray}}

            $securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force
            $credential = [pscredential]::new($username, $securePassword)

            $guestScript = {
                param($requestedInterfaceAlias, $dnsServers)
                $ErrorActionPreference = 'Stop'

                if (-not [string]::IsNullOrWhiteSpace($requestedInterfaceAlias)) {
                    $adapters = @(Get-NetAdapter -Name $requestedInterfaceAlias -ErrorAction Stop)
                }
                else {
                    $adapters = @(
                        Get-NetAdapter -ErrorAction Stop |
                        Where-Object {
                            [string]$_.Status -eq 'Up' -and
                            [string]$_.HardwareInterface -ne 'False'
                        } |
                        Sort-Object ifIndex
                    )
                }

                if (@($adapters).Count -eq 0) {
                    throw 'No active network adapter was found inside the VM.'
                }

                $adapter = @($adapters)[0]
                Set-DnsClientServerAddress `
                    -InterfaceIndex ([int]$adapter.ifIndex) `
                    -ServerAddresses @($dnsServers) `
                    -ErrorAction Stop

                Clear-DnsClientCache -ErrorAction SilentlyContinue

                $dns = Get-DnsClientServerAddress `
                    -InterfaceIndex ([int]$adapter.ifIndex) `
                    -AddressFamily IPv4 `
                    -ErrorAction Stop

                [pscustomobject]@{
                    InterfaceAlias = [string]$adapter.Name
                    DnsServers = @(
                        @($dns.ServerAddresses) |
                        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
                    )
                }
            }

            $guest = Invoke-Command `
                -VMName $vmName `
                -Credential $credential `
                -ScriptBlock $guestScript `
                -ArgumentList $interfaceAlias, (,$expectedDnsServers) `
                -ErrorAction Stop

            [pscustomobject]@{
                VmName = $vmName
                InterfaceAlias = [string]$guest.InterfaceAlias
                DnsServers = @($guest.DnsServers)
                CheckedAt = (Get-Date).ToUniversalTime().ToString('o',[System.Globalization.CultureInfo]::InvariantCulture)
            } | ConvertTo-Json -Compress -Depth 5
            """;

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<VmGuestDnsStatusJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Windows DNS synchronization returned no result.");

        return new VmGuestDnsStatus(
            result.VmName ?? profile.VmName,
            result.InterfaceAlias ?? string.Empty,
            result.DnsServers ?? [],
            ParseDateTime(result.CheckedAt));
    }

    public async Task<GuestNetworkRepairResult> RepairWindowsGuestNetworkAsync(VmNetworkPortProfile profile, string username, string password, string panelUrl)
    {
        if (string.IsNullOrWhiteSpace(profile.InternalIpAddress))
        {
            throw new InvalidOperationException("Internal VM IPv4 is required before guest internet repair can run.");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("A saved Windows username and password is required for PowerShell Direct network repair. Save the VM access details first.");
        }

        var configuredNatName = configuration["NetworkProfiles:NatName"] ?? string.Empty;
        var natName = NormalizeNatName(string.IsNullOrWhiteSpace(profile.NatName) ? configuredNatName : profile.NatName);
        // Respect the saved PenguinPanel profile exactly. An intentionally blank
        // AlternateDnsServer means "no alternate resolver"; do not silently insert
        // a public DNS server that can bypass the operator's preferred resolver.
        var preferredDns = (profile.PreferredDnsServer ?? string.Empty).Trim();
        var alternateDns = (profile.AlternateDnsServer ?? string.Empty).Trim();
        var configuredGateway = (configuration["Provisioning:Gateway"] ?? configuration["NetworkProfiles:Gateway"] ?? string.Empty).Trim();
        var panelHost = string.Empty;
        if (System.Uri.TryCreate(panelUrl, System.UriKind.Absolute, out var panelUri))
        {
            panelHost = panelUri.Host;
        }

        var script = $$"""
            $ErrorActionPreference = 'Stop';
            $vmName = {{Quote(profile.VmName)}};
            $natName = {{Quote(natName)}};
            $internalIp = {{Quote(profile.InternalIpAddress.Trim())}};
            $interfaceAlias = {{Quote(profile.DnsInterfaceAlias ?? string.Empty)}};
            $preferredDns = {{Quote(preferredDns)}};
            $alternateDns = {{Quote(alternateDns)}};
            $configuredGateway = {{Quote(configuredGateway)}};
            $panelHost = {{Quote(panelHost)}};
            $username = {{Quote(username)}};
            $plainPassword = {{Quote(password)}};

            if ([string]::IsNullOrWhiteSpace($natName)) {
                $natName = [string](@(Get-NetNat -ErrorAction SilentlyContinue | Select-Object -First 1).Name);
            }

            $vm = Get-VM -Name $vmName -ErrorAction Stop;
            if ([string]$vm.State -ne 'Running') {
                Start-VM -Name $vmName -ErrorAction Stop;
                $deadline = (Get-Date).AddSeconds(90);
                do {
                    Start-Sleep -Seconds 3;
                    $vm = Get-VM -Name $vmName -ErrorAction Stop;
                } while ([string]$vm.State -ne 'Running' -and (Get-Date) -lt $deadline);
            }

            if (-not [string]::IsNullOrWhiteSpace($natName)) {
                $switch = Get-VMSwitch -Name $natName -ErrorAction SilentlyContinue;
                if ($null -ne $switch) {
                    @(Get-VMNetworkAdapter -VMName $vmName -ErrorAction SilentlyContinue) | ForEach-Object {
                        if ([string]::IsNullOrWhiteSpace([string]$_.SwitchName) -or [string]$_.SwitchName -ne $natName) {
                            Connect-VMNetworkAdapter -VMNetworkAdapter $_ -SwitchName $natName -ErrorAction SilentlyContinue;
                        }
                    };
                }
            }

            $prefixLength = 24;
            try {
                $nat = Get-NetNat -Name $natName -ErrorAction Stop;
                if ([string]$nat.InternalIPInterfaceAddressPrefix -match '/(\d+)$') {
                    $prefixLength = [int]$Matches[1];
                }
            } catch { }

            $gateway = $configuredGateway;
            if ([string]::IsNullOrWhiteSpace($gateway) -and -not [string]::IsNullOrWhiteSpace($natName)) {
                $vEthernetAlias = 'vEthernet (' + $natName + ')';
                $hostIp = @(
                    Get-NetIPAddress -AddressFamily IPv4 -InterfaceAlias $vEthernetAlias -ErrorAction SilentlyContinue |
                    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.IPAddress) -and [string]$_.IPAddress -notlike '169.254*' } |
                    Select-Object -First 1
                ).IPAddress;
                if (-not [string]::IsNullOrWhiteSpace([string]$hostIp)) { $gateway = [string]$hostIp; }
            }

            if ([string]::IsNullOrWhiteSpace($gateway) -and $internalIp -match '^(\d+\.\d+\.\d+)\.\d+$') {
                $gateway = $Matches[1] + '.1';
            }

            $dnsServers = @($preferredDns, $alternateDns) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique;

            $securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force;
            $credential = [pscredential]::new($username, $securePassword);

            $guestScript = {
                param($guestIp, $guestPrefixLength, $guestGateway, $guestDnsServers, $guestInterfaceAlias, $guestPanelHost)
                $ErrorActionPreference = 'Stop';

                $adapters = @();
                if (-not [string]::IsNullOrWhiteSpace($guestInterfaceAlias)) {
                    $adapters = @(Get-NetAdapter -Name $guestInterfaceAlias -ErrorAction Stop);
                } else {
                    $adapters = @(
                        Get-NetAdapter -ErrorAction Stop |
                        Where-Object { [string]$_.Status -eq 'Up' -and [string]$_.HardwareInterface -ne 'False' } |
                        Sort-Object ifIndex
                    );
                }

                if (@($adapters).Count -eq 0) {
                    throw 'No active network adapter was found inside the VM. Start the adapter or provide DNS interface alias.';
                }

                $adapter = @($adapters)[0];
                $index = [int]$adapter.ifIndex;

                if (-not [string]::IsNullOrWhiteSpace($guestIp)) {
                    $hasTargetIp = @(Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $index -ErrorAction SilentlyContinue | Where-Object { [string]$_.IPAddress -eq $guestIp }).Count -gt 0;
                    if (-not $hasTargetIp) {
                        @(Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $index -ErrorAction SilentlyContinue |
                            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.IPAddress) -and [string]$_.IPAddress -notlike '169.254*' }) |
                            ForEach-Object { Remove-NetIPAddress -InterfaceIndex $index -IPAddress $_.IPAddress -Confirm:$false -ErrorAction SilentlyContinue };
                    }

                    @(Get-NetRoute -InterfaceIndex $index -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue) |
                        ForEach-Object { Remove-NetRoute -InterfaceIndex $index -DestinationPrefix '0.0.0.0/0' -NextHop $_.NextHop -Confirm:$false -ErrorAction SilentlyContinue };

                    if (-not $hasTargetIp) {
                        if ([string]::IsNullOrWhiteSpace($guestGateway)) {
                            New-NetIPAddress -InterfaceIndex $index -IPAddress $guestIp -PrefixLength $guestPrefixLength -ErrorAction Stop | Out-Null;
                        } else {
                            New-NetIPAddress -InterfaceIndex $index -IPAddress $guestIp -PrefixLength $guestPrefixLength -DefaultGateway $guestGateway -ErrorAction Stop | Out-Null;
                        }
                    } elseif (-not [string]::IsNullOrWhiteSpace($guestGateway)) {
                        New-NetRoute -DestinationPrefix '0.0.0.0/0' -InterfaceIndex $index -NextHop $guestGateway -ErrorAction SilentlyContinue | Out-Null;
                    }
                }

                if (@($guestDnsServers).Count -gt 0) {
                    Set-DnsClientServerAddress -InterfaceIndex $index -ServerAddresses $guestDnsServers -ErrorAction Stop;
                }

                Clear-DnsClientCache -ErrorAction SilentlyContinue;

                $resolved = $false;
                $reachable = $false;
                if (-not [string]::IsNullOrWhiteSpace($guestPanelHost)) {
                    try {
                        Resolve-DnsName $guestPanelHost -ErrorAction Stop | Out-Null;
                        $resolved = $true;
                    } catch { $resolved = $false; }

                    try {
                        $test = Test-NetConnection $guestPanelHost -Port 443 -WarningAction SilentlyContinue;
                        $reachable = [bool]$test.TcpTestSucceeded;
                    } catch { $reachable = $false; }
                }

                [pscustomobject]@{
                    InterfaceAlias = [string]$adapter.Name;
                    IpAddress = $guestIp;
                    Gateway = $guestGateway;
                    DnsServers = (@($guestDnsServers) -join ', ');
                    DnsResolved = [bool]$resolved;
                    PanelReachable = [bool]$reachable;
                }
            };

            $guest = Invoke-Command -VMName $vmName -Credential $credential -ScriptBlock $guestScript -ArgumentList $internalIp, $prefixLength, $gateway, $dnsServers, $interfaceAlias, $panelHost -ErrorAction Stop;
            $message = 'Guest network repair applied on ' + $guest.InterfaceAlias + '. IP ' + $guest.IpAddress + ', gateway ' + $guest.Gateway + ', DNS ' + $guest.DnsServers + '.';
            if (-not $guest.DnsResolved -or -not $guest.PanelReachable) {
                $message += ' The settings were applied, but the VM could not verify panel connectivity yet. Check gateway/NAT and DNS reachability.';
            } else {
                $message += ' DNS and panel connectivity verified.';
            }

            [pscustomobject]@{
                VmName = $vmName;
                InterfaceAlias = [string]$guest.InterfaceAlias;
                IpAddress = [string]$guest.IpAddress;
                Gateway = [string]$guest.Gateway;
                DnsServers = [string]$guest.DnsServers;
                DnsResolved = [bool]$guest.DnsResolved;
                PanelReachable = [bool]$guest.PanelReachable;
                Message = $message;
            } | ConvertTo-Json -Compress -Depth 5
            """;

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<GuestNetworkRepairJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Guest network repair returned no result.");

        return new GuestNetworkRepairResult(
            result.VmName ?? profile.VmName,
            result.InterfaceAlias ?? string.Empty,
            result.IpAddress ?? profile.InternalIpAddress,
            result.Gateway ?? string.Empty,
            result.DnsServers ?? string.Empty,
            result.DnsResolved,
            result.PanelReachable,
            result.Message ?? "Guest network repair completed.");
    }

    public async Task<(long BytesIn, long BytesOut)> GetMeteredTrafficAsync(string name)
    {
        await RunAsync($"Enable-VMResourceMetering -VMName {Quote(name)} -ErrorAction SilentlyContinue");

        // Hyper-V's NetworkMeteredTrafficReport is usually a collection of rows like:
        // LocalAddress, RemoteAddress, Direction, TotalTraffic(M). Older code tried to
        // read TotalIncomingTraffic/TotalOutgoingTraffic, which does not exist on many
        // Windows Server builds and always returned 0. Sum the per-direction rows and
        // convert the TotalTraffic value from megabytes to bytes for rate calculations.
        var script = "function Convert-ToInt64($value) { " +
            "if ($null -eq $value) { return [int64]0 } " +
            "try { return [int64][double]$value } catch { return [int64]0 } " +
            "}; " +
            "function Get-TrafficMegabytes($row) { " +
            "if ($null -eq $row) { return [int64]0 } " +
            "$prop = $row.PSObject.Properties['TotalTraffic']; " +
            "if ($null -ne $prop) { return Convert-ToInt64 $prop.Value } " +
            "$prop = $row.PSObject.Properties['TotalTraffic(M)']; " +
            "if ($null -ne $prop) { return Convert-ToInt64 $prop.Value } " +
            "return [int64]0 " +
            "}; " +
            "$meter = Measure-VM -VMName " + Quote(name) + " -ErrorAction Stop; " +
            "$report = @($meter.NetworkMeteredTrafficReport); " +
            "$bytesIn = [int64]0; $bytesOut = [int64]0; " +
            "foreach ($row in $report) { " +
            "$trafficBytes = [int64]((Get-TrafficMegabytes $row) * 1048576); " +
            "$direction = [string]$row.Direction; " +
            "if ($direction -eq 'Inbound') { $bytesIn += $trafficBytes } " +
            "elseif ($direction -eq 'Outbound') { $bytesOut += $trafficBytes } " +
            "}; " +
            "if (($bytesIn -eq 0) -and ($bytesOut -eq 0)) { " +
            "$bytesIn = Convert-ToInt64 $meter.TotalIncomingTraffic; " +
            "$bytesOut = Convert-ToInt64 $meter.TotalOutgoingTraffic; " +
            "}; " +
            "[pscustomobject]@{ BytesIn = $bytesIn; BytesOut = $bytesOut; Rows = $report.Count } | ConvertTo-Json -Compress -Depth 4";

        var output = await RunJsonAsync(script);
        if (string.IsNullOrWhiteSpace(output))
        {
            return (0, 0);
        }

        var traffic = JsonSerializer.Deserialize<TrafficJson>(output, JsonOptions);
        return (ReadInt64(traffic?.BytesIn), ReadInt64(traffic?.BytesOut));
    }

    public async Task<VmBackupSourceInfo> GetVmBackupSourceAsync(string vmName)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = {{Quote(vmName)}}
            $vm = Get-VM -Name $vmName -ErrorAction Stop
            $checkpointCount = @(Get-VMSnapshot -VMName $vmName -ErrorAction SilentlyContinue).Count
            $diskItems = @()

            foreach ($drive in @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction Stop)) {
                $path = [string]$drive.Path
                if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path)) {
                    throw ('VM disk is missing: ' + $path)
                }

                $vhd = Get-VHD -Path $path -ErrorAction Stop
                $estimated = [int64]0
                $cursor = $vhd
                $seen = @{}

                while ($null -ne $cursor) {
                    $cursorPath = [string]$cursor.Path
                    if ([string]::IsNullOrWhiteSpace($cursorPath) -or $seen.ContainsKey($cursorPath)) {
                        break
                    }

                    $seen[$cursorPath] = $true
                    $estimated += [int64]$cursor.FileSize

                    $parentPath = [string]$cursor.ParentPath
                    if ([string]::IsNullOrWhiteSpace($parentPath)) {
                        break
                    }

                    $cursor = Get-VHD -Path $parentPath -ErrorAction Stop
                }

                $diskItems += [pscustomobject]@{
                    Path = $path
                    ControllerType = [string]$drive.ControllerType
                    ControllerNumber = [int]$drive.ControllerNumber
                    ControllerLocation = [int]$drive.ControllerLocation
                    PhysicalSizeBytes = [int64]$vhd.FileSize
                    VirtualSizeBytes = [int64]$vhd.Size
                    EstimatedBackupBytes = [int64][Math]::Max($estimated, [int64]$vhd.FileSize)
                    IsDifferencing = (-not [string]::IsNullOrWhiteSpace([string]$vhd.ParentPath))
                }
            }

            [pscustomobject]@{
                VmName = $vmName
                State = [string]$vm.State
                CpuCores = [int]$vm.ProcessorCount
                MemoryStartupBytes = [int64]$vm.MemoryStartup
                Generation = [int]$vm.Generation
                CheckpointCount = [int]$checkpointCount
                Disks = @($diskItems)
            } | ConvertTo-Json -Compress -Depth 6
            """;

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<VmBackupSourceJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("VM backup source inspection returned no result.");

        return new VmBackupSourceInfo(
            item.VmName ?? vmName,
            item.State ?? "Unknown",
            item.CpuCores,
            item.MemoryStartupBytes,
            item.Generation,
            item.CheckpointCount,
            (item.Disks ?? []).Select(disk => new VmBackupDiskSource(
                disk.Path ?? string.Empty,
                disk.ControllerType ?? string.Empty,
                disk.ControllerNumber,
                disk.ControllerLocation,
                disk.PhysicalSizeBytes,
                disk.VirtualSizeBytes,
                disk.EstimatedBackupBytes,
                disk.IsDifferencing)).ToList());
    }

    public async Task<(long FileSizeBytes, long VirtualSizeBytes)> CreateStandaloneVmDiskBackupAsync(
        string sourcePath,
        string destinationPath)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $source = {{Quote(sourcePath)}}
            $destination = {{Quote(destinationPath)}}

            if (-not (Test-Path -LiteralPath $source)) {
                throw ('Source VM disk does not exist: ' + $source)
            }

            $destinationFolder = Split-Path -Parent $destination
            New-Item -ItemType Directory -Path $destinationFolder -Force | Out-Null
            if (Test-Path -LiteralPath $destination) {
                Remove-Item -LiteralPath $destination -Force -ErrorAction Stop
            }

            $sourceVhd = Get-VHD -Path $source -ErrorAction Stop
            if (-not [string]::IsNullOrWhiteSpace([string]$sourceVhd.ParentPath)) {
                Convert-VHD -Path $source -DestinationPath $destination -VHDType Dynamic -ErrorAction Stop
            }
            else {
                Copy-Item -LiteralPath $source -Destination $destination -Force -ErrorAction Stop
            }

            if (-not (Test-Path -LiteralPath $destination)) {
                throw 'Backup disk copy was not created.'
            }

            $backupVhd = Get-VHD -Path $destination -ErrorAction Stop
            [pscustomobject]@{
                FileSizeBytes = [int64]$backupVhd.FileSize
                VirtualSizeBytes = [int64]$backupVhd.Size
            } | ConvertTo-Json -Compress
            """;

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<BackupDiskCopyJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Backup disk copy returned no result.");
        return (item.FileSizeBytes, item.VirtualSizeBytes);
    }

    public async Task RestoreVmDisksFromBackupAsync(
        string vmName,
        IReadOnlyList<VmBackupDiskRestoreRequest> restoreDisks)
    {
        if (restoreDisks.Count == 0)
        {
            throw new InvalidOperationException("Backup manifest does not contain any disks.");
        }

        var planEntries = string.Join(",", restoreDisks.Select(item =>
            "[pscustomobject]@{" +
            "BackupPath=" + Quote(item.BackupPath) + ";" +
            "ControllerType=" + Quote(item.ControllerType) + ";" +
            "ControllerNumber=" + item.ControllerNumber.ToString(CultureInfo.InvariantCulture) + ";" +
            "ControllerLocation=" + item.ControllerLocation.ToString(CultureInfo.InvariantCulture) +
            "}"));

        var script =
            "$ErrorActionPreference = 'Stop'; " +
            "$vmName = " + Quote(vmName) + "; " +
            "$requested = @(" + planEntries + "); " +
            "$vm = Get-VM -Name $vmName -ErrorAction Stop; " +
            "if ([string]$vm.State -notin @('Off','Saved','Paused','Stopped')) { throw ('VM must be stopped before full backup restore. Current state: ' + [string]$vm.State) } " +
            "$snapshots = @(Get-VMSnapshot -VMName $vmName -ErrorAction SilentlyContinue); " +
            "if ($snapshots.Count -gt 0) { throw 'Full backup restore is blocked while Hyper-V checkpoints exist. Delete/merge checkpoints first so the active disk is a standalone VHD/VHDX.' } " +
            "$current = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction Stop); " +
            "if ($current.Count -ne $requested.Count) { throw ('Disk layout changed since backup. Backup disks: ' + $requested.Count + '; current disks: ' + $current.Count) } " +
            "$plans = @(); " +
            "foreach ($item in $requested) { " +
            "$drive = @($current | Where-Object { ([string]$_.ControllerType -eq [string]$item.ControllerType) -and ([int]$_.ControllerNumber -eq [int]$item.ControllerNumber) -and ([int]$_.ControllerLocation -eq [int]$item.ControllerLocation) } | Select-Object -First 1)[0]; " +
            "if ($null -eq $drive) { throw ('Current VM no longer has the backup disk controller slot ' + $item.ControllerType + ':' + $item.ControllerNumber + ':' + $item.ControllerLocation) } " +
            "$currentPath = [string]$drive.Path; " +
            "if ([System.IO.Path]::GetExtension($currentPath).ToLowerInvariant() -eq '.avhdx') { throw ('Cannot restore over active AVHDX checkpoint disk: ' + $currentPath) } " +
            "if (-not (Test-Path -LiteralPath $item.BackupPath)) { throw ('Backup disk is missing: ' + $item.BackupPath) } " +
            "$extension = [System.IO.Path]::GetExtension($currentPath); " +
            "$token = [Guid]::NewGuid().ToString('N'); " +
            "$temp = $currentPath + '.penguin-restore-' + $token + $extension; " +
            "$rollback = $currentPath + '.penguin-rollback-' + $token + $extension; " +
            "Copy-Item -LiteralPath $item.BackupPath -Destination $temp -Force -ErrorAction Stop; " +
            "if (-not (Test-Path -LiteralPath $temp)) { throw ('Restore staging copy failed for ' + $currentPath) } " +
            "$plans += [pscustomobject]@{ Current=$currentPath; Temp=$temp; Rollback=$rollback }; " +
            "} " +
            "$swapped = @(); " +
            "try { " +
            "$vmIdentity = 'NT VIRTUAL MACHINE\\' + [string]$vm.VMId; " +
            "$icacls = Join-Path $env:SystemRoot 'System32\\icacls.exe'; " +
            "foreach ($plan in $plans) { " +
            "Move-Item -LiteralPath $plan.Current -Destination $plan.Rollback -Force -ErrorAction Stop; " +
            "Move-Item -LiteralPath $plan.Temp -Destination $plan.Current -Force -ErrorAction Stop; " +
            "$swapped += $plan; " +
            "$originalAcl = Get-Acl -LiteralPath $plan.Rollback -ErrorAction Stop; " +
            "Set-Acl -LiteralPath $plan.Current -AclObject $originalAcl -ErrorAction Stop; " +
            "& $icacls $plan.Current /grant ($vmIdentity + ':F') | Out-Null; " +
            "if ($LASTEXITCODE -ne 0) { throw ('Failed to grant Hyper-V VM disk permission to ' + $vmIdentity + ' on ' + $plan.Current + '. icacls exit code: ' + $LASTEXITCODE) } " +
            "} " +
            "foreach ($plan in $plans) { if (Test-Path -LiteralPath $plan.Rollback) { Remove-Item -LiteralPath $plan.Rollback -Force -ErrorAction Stop } } " +
            "} catch { " +
            "foreach ($plan in @($swapped | Select-Object -Reverse)) { " +
            "if (Test-Path -LiteralPath $plan.Current) { Remove-Item -LiteralPath $plan.Current -Force -ErrorAction SilentlyContinue } " +
            "if (Test-Path -LiteralPath $plan.Rollback) { Move-Item -LiteralPath $plan.Rollback -Destination $plan.Current -Force -ErrorAction SilentlyContinue } " +
            "} " +
            "foreach ($plan in $plans) { if (Test-Path -LiteralPath $plan.Temp) { Remove-Item -LiteralPath $plan.Temp -Force -ErrorAction SilentlyContinue } } " +
            "throw " +
            "}";

        await RunAsync(script);
    }

    public async Task RepairVmDiskPermissionsAsync(string vmName)
    {
        var script =
            "$ErrorActionPreference = 'Stop'; " +
            "$vmName = " + Quote(vmName) + "; " +
            "$vm = Get-VM -Name $vmName -ErrorAction Stop; " +
            "$vmIdentity = 'NT VIRTUAL MACHINE\\' + [string]$vm.VMId; " +
            "$icacls = Join-Path $env:SystemRoot 'System32\\icacls.exe'; " +
            "$drives = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction Stop); " +
            "if ($drives.Count -eq 0) { throw 'VM has no attached virtual hard disks.' } " +
            "foreach ($drive in $drives) { " +
            "$path = [string]$drive.Path; " +
            "if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path)) { throw ('VM disk is missing: ' + $path) } " +
            "& $icacls $path /grant ($vmIdentity + ':F') | Out-Null; " +
            "if ($LASTEXITCODE -ne 0) { throw ('Failed to repair Hyper-V VM disk permission on ' + $path + '. icacls exit code: ' + $LASTEXITCODE) } " +
            "}";

        await RunAsync(script);
    }

    public async Task<IReadOnlyList<VmCheckpointInfo>> GetVmCheckpointsAsync(string name)
    {
        // Windows PowerShell can serialize DateTime values in a format that
        // System.Text.Json does not always parse consistently. Build a small,
        // stable DTO in PowerShell and return CreationTime as an ISO-8601 string.
        // This makes restore points show reliably after creating a checkpoint.
        var script = "$snapshots = @(Get-VMSnapshot -VMName " + Quote(name) + " -ErrorAction SilentlyContinue); " +
            "$items = foreach ($snapshot in $snapshots) { " +
            "$footprint = [int64]0; " +
            "foreach ($drive in @(Get-VMHardDiskDrive -VMSnapshot $snapshot -ErrorAction SilentlyContinue)) { " +
            "$diskPath = [string]$drive.Path; " +
            "if (-not [string]::IsNullOrWhiteSpace($diskPath) -and (Test-Path -LiteralPath $diskPath)) { " +
            "try { $vhd = Get-VHD -Path $diskPath -ErrorAction Stop; if ([System.IO.Path]::GetExtension($diskPath).Equals('.avhdx',[System.StringComparison]::OrdinalIgnoreCase)) { $footprint += [int64]$vhd.FileSize } } catch { } " +
            "} " +
            "} " +
            "[pscustomobject]@{ " +
            "Name = [string]$snapshot.Name; " +
            "Id = [string]$snapshot.Id; " +
            "SnapshotType = [string]$snapshot.SnapshotType; " +
            "CreationTime = if ($null -eq $snapshot.CreationTime) { $null } else { $snapshot.CreationTime.ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture) }; " +
            "DiskFootprintBytes = [int64]$footprint " +
            "} " +
            "}; " +
            "if (@($items).Count -eq 0) { '[]' } else { @($items) | ConvertTo-Json -Compress -Depth 4 }";

        var output = await RunJsonAsync(script);
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var items = output.TrimStart().StartsWith("[", StringComparison.Ordinal)
            ? JsonSerializer.Deserialize<List<CheckpointJson>>(output, JsonOptions) ?? []
            : [JsonSerializer.Deserialize<CheckpointJson>(output, JsonOptions)!];

        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new VmCheckpointInfo(
                item.Name!,
                item.Id?.ToString() ?? string.Empty,
                item.SnapshotType?.ToString() ?? string.Empty,
                ParseDateTime(item.CreationTime),
                item.DiskFootprintBytes))
            .OrderByDescending(item => item.CreationTime)
            .ToList();
    }

    public async Task<VmCheckpointInfo> CreateVmCheckpointAsync(
        string vmName,
        string checkpointName,
        bool preferProduction = true)
    {
        var script =
            "$ErrorActionPreference = 'Stop'; " +
            "$vmName = " + Quote(vmName) + "; " +
            "$snapshotName = " + Quote(checkpointName) + "; " +
            "$vm = Get-VM -Name $vmName -ErrorAction Stop; " +
            "$originalType = [string]$vm.CheckpointType; " +
            "$changedType = $false; " +
            "try { " +
            "if (" + (preferProduction ? "$true" : "$false") + " -and $originalType -notin @('Production','ProductionOnly')) { " +
            "Set-VM -VM $vm -CheckpointType Production -ErrorAction Stop; $changedType = $true; " +
            "} " +
            "Checkpoint-VM -VM $vm -SnapshotName $snapshotName -Confirm:$false -ErrorAction Stop | Out-Null; " +
            "} finally { " +
            "if ($changedType) { try { Set-VM -VM $vm -CheckpointType $originalType -ErrorAction Stop } catch { } } " +
            "}";

        await RunAsync(script);

        var created = (await GetVmCheckpointsAsync(vmName))
            .FirstOrDefault(item => string.Equals(item.Name, checkpointName, StringComparison.OrdinalIgnoreCase));

        return created
            ?? throw new InvalidOperationException($"Hyper-V reported success but Quick Restore Point '{checkpointName}' could not be found.");
    }

    public async Task<long> GetVmCheckpointStorageBytesAsync(string vmName)
    {
        var script =
            "$ErrorActionPreference = 'Stop'; " +
            "$vmName = " + Quote(vmName) + "; " +
            "$paths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase); " +
            "$allDrives = @(); " +
            "$allDrives += @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction SilentlyContinue); " +
            "foreach ($snapshot in @(Get-VMSnapshot -VMName $vmName -ErrorAction SilentlyContinue)) { " +
            "$allDrives += @(Get-VMHardDiskDrive -VMSnapshot $snapshot -ErrorAction SilentlyContinue); " +
            "} " +
            "foreach ($drive in $allDrives) { " +
            "$cursorPath = [string]$drive.Path; " +
            "while (-not [string]::IsNullOrWhiteSpace($cursorPath) -and (Test-Path -LiteralPath $cursorPath)) { " +
            "try { $vhd = Get-VHD -Path $cursorPath -ErrorAction Stop } catch { break } " +
            "if ([System.IO.Path]::GetExtension($cursorPath).Equals('.avhdx',[System.StringComparison]::OrdinalIgnoreCase)) { [void]$paths.Add($cursorPath) } " +
            "$cursorPath = [string]$vhd.ParentPath; " +
            "} " +
            "} " +
            "$total = [int64]0; " +
            "foreach ($path in $paths) { try { $total += [int64](Get-VHD -Path $path -ErrorAction Stop).FileSize } catch { } } " +
            "[pscustomobject]@{ TotalBytes=$total } | ConvertTo-Json -Compress";

        var output = await RunJsonAsync(script);
        var item = JsonSerializer.Deserialize<CheckpointStorageJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Quick Restore Point storage inspection returned no result.");
        return item.TotalBytes;
    }

    public Task RestoreVmCheckpointByIdAsync(string vmName, string snapshotId) =>
        RunAsync(
            "$snapshot = @(Get-VMSnapshot -VMName " + Quote(vmName) + " -ErrorAction Stop | " +
            "Where-Object { [string]$_.Id -eq " + Quote(snapshotId) + " } | Select-Object -First 1)[0]; " +
            "if ($null -eq $snapshot) { throw 'Quick Restore Point no longer exists.' }; " +
            "Restore-VMSnapshot -VMSnapshot $snapshot -Confirm:$false -ErrorAction Stop");

    public Task DeleteVmCheckpointByIdAsync(string vmName, string snapshotId) =>
        RunAsync(
            "$snapshot = @(Get-VMSnapshot -VMName " + Quote(vmName) + " -ErrorAction Stop | " +
            "Where-Object { [string]$_.Id -eq " + Quote(snapshotId) + " } | Select-Object -First 1)[0]; " +
            "if ($null -eq $snapshot) { exit 0 }; " +
            "Remove-VMSnapshot -VMSnapshot $snapshot -Confirm:$false -ErrorAction Stop");

    public Task RestoreVmCheckpointAsync(string vmName, string checkpointName) =>
        RunAsync($"Restore-VMSnapshot -VMName {Quote(vmName)} -Name {Quote(checkpointName)} -Confirm:$false");

    public Task DeleteVmCheckpointAsync(string vmName, string checkpointName) =>
        RunAsync($"Remove-VMSnapshot -VMName {Quote(vmName)} -Name {Quote(checkpointName)} -Confirm:$false");

    private static async Task<string> RunJsonAsync(string script) => await RunAsync(script);

    private static async Task<string> RunAsync(string script)
    {
        // Keep the error-handling wrapper syntactically independent from the generated command.
        // The inner command is transported as UTF-8 Base64 and compiled as its own ScriptBlock.
        // A parser error in the inner command therefore cannot corrupt the wrapper's try/catch
        // and produce a cascade of misleading secondary parser errors.
        //
        // The outer wrapper is still sent with -EncodedCommand (UTF-16LE), which is the format
        // Windows PowerShell expects.
        const string errorMarker = "__PENGUIN_PS_ERROR_B64__";
        var innerScriptB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var wrappedScript =
            "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; " +
            "$innerBytes = [Convert]::FromBase64String('" + innerScriptB64 + "'); " +
            "$innerText = [System.Text.Encoding]::UTF8.GetString($innerBytes); " +
            "try { $innerBlock = [ScriptBlock]::Create($innerText); & $innerBlock } catch { " +
            "$errorText = [string]$_.Exception.Message; " +
            "if ($_.ErrorDetails -and -not [string]::IsNullOrWhiteSpace([string]$_.ErrorDetails.Message)) { " +
            "$errorDetailsText = [string]$_.ErrorDetails.Message; " +
            "if ([string]::IsNullOrWhiteSpace($errorText)) { $errorText = $errorDetailsText } " +
            "elseif ($errorText -notlike ('*' + $errorDetailsText + '*')) { $errorText = $errorText + ' | ' + $errorDetailsText } " +
            "}; " +
            "if ([string]::IsNullOrWhiteSpace($errorText)) { $errorText = [string]$_ }; " +
            "$errorBytes = [System.Text.Encoding]::UTF8.GetBytes($errorText); " +
            "$errorB64 = [Convert]::ToBase64String($errorBytes); " +
            "[Console]::Out.WriteLine('" + errorMarker + "' + $errorB64); " +
            "[Console]::Error.WriteLine($errorText); exit 1 }";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrappedScript));

        using var process = new Process();
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);
        process.StartInfo = startInfo;

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            var detail = ExtractPowerShellErrorMarker(output, errorMarker);
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = NormalizePowerShellError(error);
            }
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = $"PowerShell exited with code {process.ExitCode} without returning error text.";
            }

            throw new InvalidOperationException(detail);
        }

        return output.Trim();
    }

    private static string ExtractPowerShellErrorMarker(string output, string marker)
    {
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(marker))
        {
            return string.Empty;
        }

        foreach (var line in output.Replace("\r", string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(marker, StringComparison.Ordinal))
            {
                continue;
            }

            var encoded = trimmed[marker.Length..].Trim();
            if (string.IsNullOrWhiteSpace(encoded))
            {
                continue;
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Trim();
            }
            catch
            {
                // Fall through to stderr/CLIXML normalization.
            }
        }

        return string.Empty;
    }

    private static string FormatByteCount(long bytes)
    {
        if (bytes <= 0) return "0 GB";
        return $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
    }

    private static string NormalizePowerShellError(string error)
    {
        var detail = (error ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        // Windows PowerShell may prepend a useful plain-text error and then append CLIXML
        // progress records (for example "Preparing modules for first use"). Keep the plain
        // message, parse only real Error stream entries from the XML, and discard Progress.
        var markerIndex = detail.IndexOf("#< CLIXML", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return detail;
        }

        var plainPrefix = detail[..markerIndex].Trim();
        var xmlText = detail[(markerIndex + "#< CLIXML".Length)..].TrimStart();
        var messages = new List<string>();
        if (!string.IsNullOrWhiteSpace(plainPrefix))
        {
            messages.Add(plainPrefix);
        }

        try
        {
            var document = System.Xml.Linq.XDocument.Parse(xmlText);
            messages.AddRange(document
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "S", StringComparison.OrdinalIgnoreCase)
                    && string.Equals((string?)element.Attribute("S"), "Error", StringComparison.OrdinalIgnoreCase))
                .Select(element => DecodeCliXmlText(element.Value).Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch
        {
            // The useful plain-text prefix is still preferable to dumping raw CLIXML into WHMCS.
        }

        var cleaned = messages
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return cleaned.Length > 0 ? string.Join(Environment.NewLine, cleaned) : "PowerShell command failed.";
    }

    private static string DecodeCliXmlText(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("_x", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return System.Text.RegularExpressions.Regex.Replace(
            value,
            @"_x([0-9A-Fa-f]{4})_",
            match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
    }

    private static void ValidateGuestCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Save the Windows username and password on the Access tab first. Windows Firewall management uses Hyper-V PowerShell Direct.");
        }
    }

    private static string NormalizeFirewallProtocol(string? protocol)
    {
        var value = (protocol ?? string.Empty).Trim().ToUpperInvariant();
        return value switch
        {
            "TCP" => "TCP",
            "UDP" => "UDP",
            _ => throw new InvalidOperationException("Windows Firewall protocol must be TCP or UDP.")
        };
    }

    private static string NormalizeFirewallAction(string? action)
    {
        var value = (action ?? string.Empty).Trim();
        if (string.Equals(value, "Allow", StringComparison.OrdinalIgnoreCase)) return "Allow";
        if (string.Equals(value, "Block", StringComparison.OrdinalIgnoreCase)) return "Block";
        throw new InvalidOperationException("Windows Firewall action must be Allow or Block.");
    }

    private static string Quote(string value) => $"'{value.Replace("'", "''")}'";

    private static string NormalizeNatName(string? natName)
    {
        var value = (natName ?? string.Empty).Trim();
        const string adapterPrefix = "vEthernet (";
        if (value.StartsWith(adapterPrefix, StringComparison.OrdinalIgnoreCase) && value.EndsWith(")", StringComparison.Ordinal))
        {
            value = value[adapterPrefix.Length..^1].Trim();
        }

        return value;
    }

    private static TimeSpan ParseTimeSpan(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && TimeSpan.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("Ticks", out var ticks))
        {
            return TimeSpan.FromTicks(ticks.GetInt64());
        }

        return TimeSpan.Zero;
    }

    private static long ReadInt64(JsonElement? element)
    {
        if (element is null)
        {
            return 0;
        }

        var value = element.Value;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when value.TryGetUInt64(out var ulongValue) => ulongValue > long.MaxValue ? long.MaxValue : (long)ulongValue,
            JsonValueKind.Number when value.TryGetDouble(out var doubleValue) => doubleValue <= 0 ? 0 : (long)Math.Min(doubleValue, long.MaxValue),
            JsonValueKind.String when long.TryParse(value.GetString(), out var stringValue) => stringValue,
            _ => 0
        };
    }

    private IEnumerable<PortMapping> BuildPortMappings(VmNetworkPortProfile profile)
    {
        if (profile.EnableRdp && profile.RdpExternalPort is > 0 && profile.RdpInternalPort > 0)
        {
            yield return new PortMapping("TCP", profile.RdpExternalPort.Value, profile.RdpInternalPort, "RDP");
            yield return new PortMapping("UDP", profile.RdpExternalPort.Value, profile.RdpInternalPort, "RDP");
        }

        if (profile.EnableFiveM && profile.FiveMExternalPort is > 0 && profile.FiveMInternalPort > 0)
        {
            yield return new PortMapping("TCP", profile.FiveMExternalPort.Value, profile.FiveMInternalPort, "FiveM");
            yield return new PortMapping("UDP", profile.FiveMExternalPort.Value, profile.FiveMInternalPort, "FiveM");
        }

        foreach (var item in ParseExtraPorts(profile.ExtraTcpPorts, "TCP"))
        {
            yield return item;
        }

        foreach (var item in ParseExtraPorts(profile.ExtraUdpPorts, "UDP"))
        {
            yield return item;
        }
    }

    private static IEnumerable<PortMapping> ParseExtraPorts(string? raw, string protocol)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        var parts = raw.Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var pair = part.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pair.Length == 1 && int.TryParse(pair[0], out var samePort) && IsValidPort(samePort))
            {
                yield return new PortMapping(protocol, samePort, samePort, "Custom");
            }
            else if (pair.Length == 2 && int.TryParse(pair[0], out var externalPort) && int.TryParse(pair[1], out var internalPort) && IsValidPort(externalPort) && IsValidPort(internalPort))
            {
                yield return new PortMapping(protocol, externalPort, internalPort, "Custom");
            }
        }
    }

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    private static string NormalizeProtocolName(string? protocol)
    {
        var value = (protocol ?? string.Empty).Trim().ToUpperInvariant();
        return value switch
        {
            "6" => "TCP",
            "17" => "UDP",
            _ => value
        };
    }


    private sealed record PortMapping(string Protocol, int ExternalPort, int InternalPort, string Label);

    private sealed class GuestFirewallRuleJson
    {
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Group { get; set; }
        public string? Protocol { get; set; }
        public string? LocalPort { get; set; }
        public string? RemoteAddress { get; set; }
        public string? Action { get; set; }
        public bool Enabled { get; set; }
        public bool IsAutomatic { get; set; }
    }

    private sealed class GuestFirewallApplyJson
    {
        public string? VmName { get; set; }
        public int RulesApplied { get; set; }
        public string? Message { get; set; }
    }

    private sealed class CleanupCountJson
    {
        public int Removed { get; set; }
    }

    private sealed class NetworkProfileCleanupJson
    {
        public string? VmName { get; set; }
        public int NatMappingsRemoved { get; set; }
        public int HostFirewallRulesRemoved { get; set; }
        public string? Message { get; set; }
    }

    private sealed class VmDeleteJson
    {
        public string? VmName { get; set; }
        public string? VmPath { get; set; }
        public int NatMappingsRemoved { get; set; }
        public int HostFirewallRulesRemoved { get; set; }
        public bool StorageDeleted { get; set; }
        public string? Message { get; set; }
    }

    private sealed class VmRdpListenerJson
    {
        public int Port { get; set; }
        public bool RemoteDesktopEnabled { get; set; }
        public bool TermServiceRunning { get; set; }
        public bool TcpListening { get; set; }
        public bool UdpListening { get; set; }
        public string? RestartError { get; set; }
        public string? CheckedAt { get; set; }
    }

    private sealed class VmProvisionJson
    {
        public string? VmName { get; set; }
        public string? VmPath { get; set; }
        public string? DiskPath { get; set; }
        public string? SwitchName { get; set; }
        public string? State { get; set; }
        public long DiskSizeBytes { get; set; }
        public string? Message { get; set; }
    }

    private sealed class VmGuestDnsStatusJson
    {
        public string? VmName { get; set; }
        public string? InterfaceAlias { get; set; }
        public List<string>? DnsServers { get; set; }
        public string? CheckedAt { get; set; }
    }

    private sealed class GuestNetworkRepairJson
    {
        public string? VmName { get; set; }
        public string? InterfaceAlias { get; set; }
        public string? IpAddress { get; set; }
        public string? Gateway { get; set; }
        public string? DnsServers { get; set; }
        public bool DnsResolved { get; set; }
        public bool PanelReachable { get; set; }
        public string? Message { get; set; }
    }

    private sealed class NetworkProfileApplyJson
    {
        public string? VmName { get; set; }
        public int RulesApplied { get; set; }
        public string? Message { get; set; }
    }

    private sealed class ConsistencyHostInventoryJson
    {
        public string? NatName { get; set; }
        public bool NatExists { get; set; }
        public List<ConsistencyHostVmJson>? Vms { get; set; }
        public List<ConsistencyNatMappingJson>? NatMappings { get; set; }
        public List<ConsistencyFirewallRuleJson>? ManagedFirewallRules { get; set; }
        public List<ConsistencyStorageDiskJson>? StorageDisks { get; set; }
        public string? CheckedAt { get; set; }
    }

    private sealed class ConsistencyHostVmJson
    {
        public string? Name { get; set; }
        public string? State { get; set; }
        public string? VmPath { get; set; }
        public List<string>? DiskPaths { get; set; }
    }

    private sealed class ConsistencyNatMappingJson
    {
        public string? NatName { get; set; }
        public string? Protocol { get; set; }
        public string? ExternalIpAddress { get; set; }
        public int ExternalPort { get; set; }
        public string? InternalIpAddress { get; set; }
        public int InternalPort { get; set; }
    }

    private sealed class ConsistencyFirewallRuleJson
    {
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Group { get; set; }
        public string? Protocol { get; set; }
        public string? LocalPort { get; set; }
        public string? Direction { get; set; }
        public string? Action { get; set; }
        public bool Enabled { get; set; }
    }

    private sealed class ConsistencyStorageDiskJson
    {
        public string? Root { get; set; }
        public string? Path { get; set; }
        public long SizeBytes { get; set; }
    }

    private sealed class VmHostInfrastructureHealthJson
    {
        public string? VmName { get; set; }
        public bool VmExists { get; set; }
        public string? VmState { get; set; }
        public string? DiskPath { get; set; }
        public bool DiskExists { get; set; }
        public string? NatName { get; set; }
        public bool NatExists { get; set; }
        public string? ExpectedInternalIp { get; set; }
        public List<string>? HyperVReportedIps { get; set; }
        public bool InternalIpObserved { get; set; }
        public List<VmHostPortHealthJson>? Ports { get; set; }
        public string? CheckedAt { get; set; }
    }

    private sealed class VmHostPortHealthJson
    {
        public string? Label { get; set; }
        public string? Protocol { get; set; }
        public int ExternalPort { get; set; }
        public int InternalPort { get; set; }
        public bool NatHealthy { get; set; }
        public bool HostFirewallHealthy { get; set; }
        public string? NatDetails { get; set; }
        public string? HostFirewallDetails { get; set; }
    }

    private sealed class VmJson
    {
        public string? Name { get; set; }
        public object? State { get; set; }
        public int CPUUsage { get; set; }
        public long MemoryAssigned { get; set; }
        public JsonElement Uptime { get; set; }
        public object? Status { get; set; }
    }

    private sealed class VmResourceJson
    {
        public string? VmName { get; set; }
        public string? State { get; set; }
        public string? PreviousState { get; set; }
        public string? FinalState { get; set; }
        public int CpuCores { get; set; }
        public int MemoryMb { get; set; }
        public int OldCpuCores { get; set; }
        public int NewCpuCores { get; set; }
        public int OldMemoryMb { get; set; }
        public int NewMemoryMb { get; set; }
        public string? DiskPath { get; set; }
        public long DiskSizeBytes { get; set; }
        public long OldDiskSizeBytes { get; set; }
        public long NewDiskSizeBytes { get; set; }
        public bool Restarted { get; set; }
        public string? Message { get; set; }
    }

    private sealed class GuestPartitionResizeJson
    {
        public bool Changed { get; set; }
        public long SizeBytes { get; set; }
    }

    private sealed class HostNodeStatusJson
    {
        public double CpuPercent { get; set; }
        public long MemoryTotalBytes { get; set; }
        public long MemoryAvailableBytes { get; set; }
        public long MemoryUsedBytes { get; set; }
        public long DiskTotalBytes { get; set; }
        public long DiskFreeBytes { get; set; }
        public int RunningVmCount { get; set; }
        public int StoppedVmCount { get; set; }
        public string? HyperVServiceStatus { get; set; }
        public string? UpdatedAt { get; set; }
    }

    private sealed class LiveMonitoringBatchJson
    {
        public HostNodeStatusJson? Host { get; set; }
        public List<VmMonitoringSampleJson>? Vms { get; set; }
        public string? SampledAt { get; set; }
    }

    private sealed class VmMonitoringSampleJson
    {
        public string? VmName { get; set; }
        public string? State { get; set; }
        public string? Status { get; set; }
        public int CpuUsagePercent { get; set; }
        public long MemoryAssignedBytes { get; set; }
        public long MemoryDemandBytes { get; set; }
        public double UptimeSeconds { get; set; }
        public string? HeartbeatStatus { get; set; }
        public bool HeartbeatHealthy { get; set; }
        public int NetworkAdapterCount { get; set; }
        public int ConnectedNetworkAdapterCount { get; set; }
        public bool ResourceMeteringEnabled { get; set; }
        public long NetworkReceivedBytes { get; set; }
        public long NetworkSentBytes { get; set; }
        public long DiskReadBytes { get; set; }
        public long DiskWrittenBytes { get; set; }
        public string? SampledAt { get; set; }
    }

    private static DateTime ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTime.MinValue;
        }

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private sealed class VmBackupSourceJson
    {
        public string? VmName { get; set; }
        public string? State { get; set; }
        public int CpuCores { get; set; }
        public long MemoryStartupBytes { get; set; }
        public int Generation { get; set; }
        public int CheckpointCount { get; set; }
        public List<VmBackupDiskSourceJson>? Disks { get; set; }
    }

    private sealed class VmBackupDiskSourceJson
    {
        public string? Path { get; set; }
        public string? ControllerType { get; set; }
        public int ControllerNumber { get; set; }
        public int ControllerLocation { get; set; }
        public long PhysicalSizeBytes { get; set; }
        public long VirtualSizeBytes { get; set; }
        public long EstimatedBackupBytes { get; set; }
        public bool IsDifferencing { get; set; }
    }

    private sealed class BackupDiskCopyJson
    {
        public long FileSizeBytes { get; set; }
        public long VirtualSizeBytes { get; set; }
    }

    private sealed class CheckpointJson
    {
        public string? Name { get; set; }
        public object? Id { get; set; }
        public object? SnapshotType { get; set; }
        public string? CreationTime { get; set; }
        public long DiskFootprintBytes { get; set; }
    }

    private sealed class CheckpointStorageJson
    {
        public long TotalBytes { get; set; }
    }

    private sealed class TemplateCreateJson
    {
        public string? TemplatePath { get; set; }
        public bool WasRunning { get; set; }
        public int Generation { get; set; }
        public string? Message { get; set; }
    }

    private sealed class TemplateValidationSandboxJson
    {
        public string? VmName { get; set; }
        public string? WorkingDirectory { get; set; }
        public string? DifferencingDiskPath { get; set; }
        public long VirtualDiskBytes { get; set; }
        public long PhysicalDiskBytes { get; set; }
        public long TemplateLastWriteUtcTicks { get; set; }
        public int Generation { get; set; }
    }

    private sealed class TemplateValidationGuestJson
    {
        public string? ComputerName { get; set; }
        public string? WindowsProductName { get; set; }
        public string? WindowsVersion { get; set; }
        public string? BuildNumber { get; set; }
        public string? Architecture { get; set; }
        public bool RdpServiceFound { get; set; }
        public bool RdpEnabled { get; set; }
        public string? RdpServiceStatus { get; set; }
    }

    private sealed class ReinstallPreflightJson
    {
        public string? VmName { get; set; }
        public string? TemplateName { get; set; }
        public string? CurrentDiskPath { get; set; }
        public long CurrentVirtualDiskBytes { get; set; }
        public long CurrentPhysicalDiskBytes { get; set; }
        public string? CurrentDiskParentPath { get; set; }
        public bool CurrentDiskIsDifferencing { get; set; }
        public long TemplateVirtualDiskBytes { get; set; }
        public long TemplatePhysicalDiskBytes { get; set; }
        public string? TemplateParentPath { get; set; }
        public int CheckpointCount { get; set; }
        public long TargetFreeBytes { get; set; }
        public long TargetTotalBytes { get; set; }
    }

    private sealed class ReinstallRollbackRestoreJson
    {
        public string? VmName { get; set; }
        public string? RestoredDiskPath { get; set; }
        public string? RemovedReinstalledDiskPath { get; set; }
        public string? PreviousState { get; set; }
        public string? FinalState { get; set; }
        public bool WasRunning { get; set; }
        public string? Message { get; set; }
    }

    private sealed class ReinstallResultJson
    {
        public string? VmName { get; set; }
        public string? TemplateName { get; set; }
        public string? TemplatePath { get; set; }
        public string? NewDiskPath { get; set; }
        public string? OldDiskRollbackPath { get; set; }
        public long DiskSizeBytes { get; set; }
        public bool WasRunning { get; set; }
        public string? PreviousState { get; set; }
        public string? FinalState { get; set; }
        public string? Message { get; set; }
    }

    private sealed class TrafficJson
    {
        public JsonElement BytesIn { get; set; }
        public JsonElement BytesOut { get; set; }
    }
}
