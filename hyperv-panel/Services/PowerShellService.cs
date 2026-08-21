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
            try { $cpu = [math]::Round([double]((Get-CimInstance Win32_Processor -ErrorAction Stop | Measure-Object -Property LoadPercentage -Average).Average), 1) } catch { $cpu = 0 }
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
        var script =
            "$vmName = " + Quote(vmName) + "; " +
            "$username = " + Quote(username) + "; " +
            "$plainPassword = " + Quote(password) + "; " +
            "$timeoutSeconds = " + Math.Clamp(timeoutSeconds, 30, 600).ToString(CultureInfo.InvariantCulture) + "; " +
            "$secure = ConvertTo-SecureString $plainPassword -AsPlainText -Force; " +
            "$credential = [pscredential]::new($username, $secure); " +
            "$deadline = (Get-Date).AddSeconds($timeoutSeconds); " +
            "$lastError = ''; " +
            "do { " +
            "try { $ready = Invoke-Command -VMName $vmName -Credential $credential -ErrorAction Stop -ScriptBlock { 'ready' }; if ([string]$ready -eq 'ready') { return } } catch { $lastError = $_.Exception.Message } " +
            "Start-Sleep -Seconds 5; " +
            "} while ((Get-Date) -lt $deadline); " +
            "throw (\"PowerShell Direct did not become ready in time. Last error: \" + $lastError)";
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
                [pscustomobject]@{
                    Port = $port;
                    RemoteDesktopEnabled = ($deny -eq 0);
                    TermServiceRunning = ($null -ne $service -and [string]$service.Status -eq 'Running');
                    TcpListening = [bool]$listening;
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
                $ruleName = 'PenguinPanel-RDP-Listener-TCP-' + $newPort;

                try {
                    # Open the new guest-side port before changing/restarting Remote Desktop Services.
                    Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue |
                        Remove-NetFirewallRule -ErrorAction SilentlyContinue;
                    New-NetFirewallRule -Name $ruleName `
                        -DisplayName ('PenguinPanel RDP listener TCP ' + $newPort) `
                        -Group 'PenguinPanel RDP Listener' `
                        -Direction Inbound -Action Allow -Enabled True -Profile Any `
                        -Protocol TCP -LocalPort $newPort -ErrorAction Stop | Out-Null;

                    Set-ItemProperty -Path $terminalServerPath -Name fDenyTSConnections -Value 0 -ErrorAction Stop;
                    Set-ItemProperty -Path $rdpTcpPath -Name PortNumber -Value $newPort -ErrorAction Stop;
                }
                catch {
                    # Only roll back when writing the actual configuration failed. A service restart
                    # failure is handled by the Hyper-V host with a one-time VM restart fallback.
                    if ($changed -and $oldPort -gt 0) {
                        try {
                            Set-ItemProperty -Path $rdpTcpPath -Name PortNumber -Value $oldPort -ErrorAction SilentlyContinue;
                            Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue |
                                Remove-NetFirewallRule -ErrorAction SilentlyContinue;
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
                do {
                    Start-Sleep -Milliseconds 500;
                    $service = Get-Service -Name TermService -ErrorAction SilentlyContinue;
                    $listening = @(Get-NetTCPConnection -State Listen -LocalPort $newPort -ErrorAction SilentlyContinue).Count -gt 0;
                    if ($listening -and $null -ne $service -and [string]$service.Status -eq 'Running') { break }
                } while ((Get-Date) -lt $deadline);

                if ($listening) {
                    # Clean up older dedicated PenguinPanel RDP-listener rules after success.
                    @(Get-NetFirewallRule -Group 'PenguinPanel RDP Listener' -ErrorAction SilentlyContinue |
                        Where-Object { [string]$_.Name -ne $ruleName }) |
                        Remove-NetFirewallRule -ErrorAction SilentlyContinue;
                }

                [pscustomobject]@{
                    Port = $newPort;
                    RemoteDesktopEnabled = $true;
                    TermServiceRunning = ($null -ne $service -and [string]$service.Status -eq 'Running');
                    TcpListening = [bool]$listening;
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
                $keepName = 'PenguinPanel-RDP-Listener-TCP-' + [int]$port;
                @(Get-NetFirewallRule -Group 'PenguinPanel RDP Listener' -ErrorAction SilentlyContinue |
                    Where-Object { [string]$_.Name -ne $keepName }) |
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
            "[pscustomobject]@{ TemplatePath = $dest; WasRunning = $wasRunning; Message = \"Template created from $vmName primary disk. Source VM was stopped while copying and then restored to its previous power state.\" } | ConvertTo-Json -Compress -Depth 4 " +
            "} finally { if ($wasRunning) { Start-VM -Name $vmName -ErrorAction SilentlyContinue | Out-Null } }";

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<TemplateCreateJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Template creation returned no result.");
        return new VmTemplateCreateResult(result.TemplatePath ?? string.Empty, result.WasRunning, result.Message ?? "Template created.");
    }

    public async Task<VmReinstallResult> ReinstallVmFromTemplateDiskAsync(string vmName, string templateName, string templateDiskPath, bool startAfter = true)
    {
        var script =
            "$vmName = " + Quote(vmName) + "; " +
            "$templateName = " + Quote(templateName) + "; " +
            "$templateDiskPath = " + Quote(templateDiskPath) + "; " +
            "$startAfter = $" + (startAfter ? "true" : "false") + "; " +
            "if (-not (Test-Path -LiteralPath $templateDiskPath)) { throw \"Template disk was not found: $templateDiskPath\" } " +
            "$ext = [System.IO.Path]::GetExtension($templateDiskPath).ToLowerInvariant(); if ($ext -notin @('.vhd','.vhdx')) { throw \"Automatic reinstall supports VHD/VHDX templates only. ISO templates can be stored for manual reinstall requests.\" } " +
            "$vm = Get-VM -Name $vmName -ErrorAction Stop; $previousState = [string]$vm.State; $wasRunning = $previousState -in @('Running','Starting','Resuming'); " +
            "if ($wasRunning) { Stop-VM -Name $vmName -Force -Confirm:$false -ErrorAction Stop; } " +
            "$deadline = (Get-Date).AddSeconds(120); do { $state = [string](Get-VM -Name $vmName -ErrorAction Stop).State; if ($state -notin @('Running','Stopping','Starting','Resuming','Pausing')) { break }; Start-Sleep -Seconds 2 } while ((Get-Date) -lt $deadline); " +
            "$state = [string](Get-VM -Name $vmName -ErrorAction Stop).State; if ($state -in @('Running','Stopping','Starting','Resuming','Pausing')) { throw \"The VM did not stop in time. Current state: $state\" } " +
            "$disk = @(Get-VMHardDiskDrive -VMName $vmName -ErrorAction Stop | Select-Object -First 1)[0]; if ($null -eq $disk) { throw \"No virtual hard disk was found on this VM.\" } " +
            "$oldPath = [string]$disk.Path; $targetFolder = Split-Path -Parent $oldPath; if ([string]::IsNullOrWhiteSpace($targetFolder) -or -not (Test-Path -LiteralPath $targetFolder)) { $targetFolder = Split-Path -Parent $templateDiskPath } " +
            "$safeVm = ($vmName -replace '[\\/:*?\"<>|]', '-').Trim(); $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'; $newDiskPath = Join-Path $targetFolder ($safeVm + '-reinstall-' + $timestamp + $ext); " +
            "Copy-Item -LiteralPath $templateDiskPath -Destination $newDiskPath -Force -ErrorAction Stop; " +
            "Set-VMHardDiskDrive -VMName $vmName -ControllerType $disk.ControllerType -ControllerNumber $disk.ControllerNumber -ControllerLocation $disk.ControllerLocation -Path $newDiskPath -ErrorAction Stop; " +
            "if ($startAfter) { Start-VM -Name $vmName -ErrorAction Stop; } " +
            "$finalState = [string](Get-VM -Name $vmName -ErrorAction Stop).State; " +
            "[pscustomobject]@{ VmName=$vmName; TemplateName=$templateName; TemplatePath=$templateDiskPath; NewDiskPath=$newDiskPath; WasRunning=$wasRunning; PreviousState=$previousState; FinalState=$finalState; Message=\"Reinstall completed from template '$templateName'. Old disk was preserved at $oldPath.\" } | ConvertTo-Json -Compress -Depth 4";

        var output = await RunJsonAsync(script);
        var result = JsonSerializer.Deserialize<ReinstallResultJson>(output, JsonOptions)
            ?? throw new InvalidOperationException("Reinstall returned no result.");

        return new VmReinstallResult(
            result.VmName ?? vmName,
            result.TemplateName ?? templateName,
            result.TemplatePath ?? templateDiskPath,
            result.NewDiskPath ?? string.Empty,
            result.WasRunning,
            result.PreviousState ?? string.Empty,
            result.FinalState ?? string.Empty,
            result.Message ?? "Reinstall completed.");
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
            "Add-NetNatStaticMapping -NatName $natName -Protocol $protocol -ExternalIPAddress $natBindAddress -ExternalPort ([int]$externalPort) -InternalIPAddress $internalIp -InternalPort ([int]$internalPort) -ErrorAction Stop | Out-Null; " +
            "} " +
            "$verified = @(Get-NetNatStaticMapping -NatName $natName -ErrorAction SilentlyContinue | Where-Object { ([string]$_.Protocol).ToUpperInvariant() -eq $protocol -and [int]$_.ExternalPort -eq [int]$externalPort -and ([string]$_.InternalIPAddress -eq $internalIp) -and [int]$_.InternalPort -eq [int]$internalPort }); " +
            "if ($verified.Count -eq 0) { throw ('WinNAT did not retain the requested ' + $protocol + '/' + $externalPort + ' mapping for ' + $vmName + '.') } " +
            "$ruleName = ('PenguinPanel ' + $vmName + ' ' + $label + ' ' + $protocol + ' ' + $externalPort); " +
            "Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue; " +
            "New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol $protocol -LocalPort ([int]$externalPort) -Action Allow | Out-Null; " +
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
            "$rulePrefix = 'PenguinPanel ' + $vmName + ' '; " +
            "$rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { ([string]$_.DisplayName).StartsWith($rulePrefix, [System.StringComparison]::OrdinalIgnoreCase) }); " +
            "foreach ($rule in $rules) { Remove-NetFirewallRule -Name $rule.Name -ErrorAction Stop; $removedFirewallRules++ } " +
            "$remainingRules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { ([string]$_.DisplayName).StartsWith($rulePrefix, [System.StringComparison]::OrdinalIgnoreCase) }); " +
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
            "@(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { [string]$_.Group -eq 'PenguinPanel Auto' }) | Remove-NetFirewallRule -ErrorAction SilentlyContinue; " +
            "$count = 0; foreach ($spec in @($ruleSpecs)) { " +
            "$safeLabel = ([string]$spec.Label -replace '[^A-Za-z0-9 _.-]', '-').Trim(); if ([string]::IsNullOrWhiteSpace($safeLabel)) { $safeLabel = 'Port' }; " +
            "$ruleName = 'PenguinPanel-Auto-' + [guid]::NewGuid().ToString('N'); " +
            "$displayName = 'PenguinPanel Auto - ' + $safeLabel + ' ' + [string]$spec.Protocol + '/' + [string]$spec.Port; " +
            "New-NetFirewallRule -Name $ruleName -DisplayName $displayName -Group 'PenguinPanel Auto' -Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol ([string]$spec.Protocol) -LocalPort ([int]$spec.Port) -ErrorAction Stop | Out-Null; $count++ } " +
            "[pscustomobject]@{ RulesApplied=$count } }; ";

        var ruleSpecs = mappings
            .Select(mapping => $"[pscustomobject]@{{ Protocol={Quote(mapping.Protocol)}; Port={mapping.InternalPort.ToString(CultureInfo.InvariantCulture)}; Label={Quote(mapping.Label)} }}")
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
        var preferredDns = string.IsNullOrWhiteSpace(profile.PreferredDnsServer) ? "1.1.1.1" : profile.PreferredDnsServer.Trim();
        var alternateDns = string.IsNullOrWhiteSpace(profile.AlternateDnsServer) ? "9.9.9.9" : profile.AlternateDnsServer.Trim();
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
            if (@($dnsServers).Count -eq 0) { $dnsServers = @('1.1.1.1', '9.9.9.9'); }

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

    public async Task<IReadOnlyList<VmCheckpointInfo>> GetVmCheckpointsAsync(string name)
    {
        // Windows PowerShell can serialize DateTime values in a format that
        // System.Text.Json does not always parse consistently. Build a small,
        // stable DTO in PowerShell and return CreationTime as an ISO-8601 string.
        // This makes restore points show reliably after creating a checkpoint.
        var script = "$snapshots = @(Get-VMSnapshot -VMName " + Quote(name) + " -ErrorAction SilentlyContinue); " +
            "$items = foreach ($snapshot in $snapshots) { " +
            "[pscustomobject]@{ " +
            "Name = [string]$snapshot.Name; " +
            "Id = [string]$snapshot.Id; " +
            "SnapshotType = [string]$snapshot.SnapshotType; " +
            "CreationTime = if ($null -eq $snapshot.CreationTime) { $null } else { $snapshot.CreationTime.ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture) } " +
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
                ParseDateTime(item.CreationTime)))
            .OrderByDescending(item => item.CreationTime)
            .ToList();
    }

    public Task CreateVmCheckpointAsync(string vmName, string checkpointName) =>
        RunAsync($"Checkpoint-VM -Name {Quote(vmName)} -SnapshotName {Quote(checkpointName)}");

    public Task RestoreVmCheckpointAsync(string vmName, string checkpointName) =>
        RunAsync($"Restore-VMSnapshot -VMName {Quote(vmName)} -Name {Quote(checkpointName)} -Confirm:$false");

    public Task DeleteVmCheckpointAsync(string vmName, string checkpointName) =>
        RunAsync($"Remove-VMSnapshot -VMName {Quote(vmName)} -Name {Quote(checkpointName)} -Confirm:$false");

    private static async Task<string> RunJsonAsync(string script) => await RunAsync(script);

    private static async Task<string> RunAsync(string script)
    {
        // Do not pass large/nested PowerShell snippets through -Command "...". Quoting there is
        // fragile (especially with nested guest scripts) and can hide the real parser/cmdlet error.
        // EncodedCommand uses UTF-16LE, which is the format Windows PowerShell expects.
        // The wrapper catches terminating errors and writes only Exception.Message to stderr so
        // callers receive a useful diagnostic without echoing the full script (which may contain
        // guest credentials).
        var wrappedScript =
            "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; try { & { " + script +
            " } } catch { [Console]::Error.WriteLine([string]$_.Exception.Message); exit 1 }";
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
            var detail = NormalizePowerShellError(error);
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = $"PowerShell exited with code {process.ExitCode} without returning error text.";
            }

            throw new InvalidOperationException(detail);
        }

        return output.Trim();
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

    private static IEnumerable<PortMapping> BuildPortMappings(VmNetworkPortProfile profile)
    {
        if (profile.EnableRdp && profile.RdpExternalPort is > 0 && profile.RdpInternalPort > 0)
        {
            yield return new PortMapping("TCP", profile.RdpExternalPort.Value, profile.RdpInternalPort, "RDP");
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

    private sealed class CheckpointJson
    {
        public string? Name { get; set; }
        public object? Id { get; set; }
        public object? SnapshotType { get; set; }
        public string? CreationTime { get; set; }
    }

    private sealed class TemplateCreateJson
    {
        public string? TemplatePath { get; set; }
        public bool WasRunning { get; set; }
        public string? Message { get; set; }
    }

    private sealed class ReinstallResultJson
    {
        public string? VmName { get; set; }
        public string? TemplateName { get; set; }
        public string? TemplatePath { get; set; }
        public string? NewDiskPath { get; set; }
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
