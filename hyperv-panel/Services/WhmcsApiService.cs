using System.Net.Mail;
using System.Security.Cryptography;

namespace HyperVPanel.Services;

public sealed class WhmcsApiService(AppDatabase db, PanelService panel)
{
    private static readonly PanelUser ApiAdmin = new(0, "WHMCS API", "admin", UserVmPermissions.Admin);
    private readonly SemaphoreSlim _createLock = new(1, 1);

    public async Task<WhmcsCreateVpsResponse> CreateAsync(WhmcsCreateVpsRequest request)
    {
        ValidateCreateRequest(request);

        await _createLock.WaitAsync();
        try
        {
            var existing = await db.GetWhmcsServiceAsync(request.ServiceId);
            if (existing is not null)
            {
                // Never let the same WHMCS service id silently move between customers.
                // This is defensive: WHMCS service ids are normally immutable, but a stale
                // or manually-edited record must not be used to provision for another client.
                if (existing.ClientId != request.ClientId)
                {
                    throw new InvalidOperationException(
                        $"WHMCS service {request.ServiceId} is already recorded for client {existing.ClientId}, not client {request.ClientId}. Manual review is required.");
                }

                await SyncWhmcsCustomerEmailAsync(existing.PanelUserId, request.CustomerEmail);

                // A failed core Hyper-V provisioning attempt is safe to retry only when the
                // recorded VM does not exist. ProvisionVmFromTemplateAsync already performs
                // conservative cleanup of a partially-created VM/folder before returning an
                // error. If a VM does exist, preserve idempotency and force an admin review.
                if (string.Equals(existing.Status, "error", StringComparison.OrdinalIgnoreCase))
                {
                    var failedVm = await panel.GetVmAsync(ApiAdmin, existing.VmName);
                    if (failedVm is null)
                    {
                        await db.DeleteFailedWhmcsServiceAsync(request.ServiceId);
                        await db.AddAuditAsync(
                            "WHMCS_PROVISION_RETRY_RESET",
                            "whmcs-service",
                            request.ServiceId.ToString(),
                            $"Cleared failed idempotency record for VM {existing.VmName} because no Hyper-V VM exists. A new create attempt may proceed.");
                    }
                    else
                    {
                        return await BuildExistingCreateResponseAsync(existing);
                    }
                }
                else if (string.Equals(existing.Status, "terminated", StringComparison.OrdinalIgnoreCase))
                {
                    // WHMCS admins can legitimately press Create again on a terminated
                    // service. Termination intentionally keeps the historical mapping row,
                    // which previously caused the generic duplicate guard to block this.
                    // Recreate is safe only when the old VM is truly gone.
                    var terminatedVm = await panel.GetVmAsync(ApiAdmin, existing.VmName);
                    if (terminatedVm is null)
                    {
                        await db.DeleteTerminatedWhmcsServiceForRecreateAsync(request.ServiceId);
                        await db.AddAuditAsync(
                            "WHMCS_TERMINATED_SERVICE_RECREATE_RESET",
                            "whmcs-service",
                            request.ServiceId.ToString(),
                            $"Cleared terminated mapping for former VM {existing.VmName}. WHMCS requested a fresh Create for the same service id/client.");
                    }
                    else
                    {
                        return await BuildExistingCreateResponseAsync(existing);
                    }
                }
                else
                {
                    // Active, provisioning, suspended, and unknown non-terminal states retain
                    // strict idempotency. A second Create must never clone a duplicate VPS.
                    return await BuildExistingCreateResponseAsync(existing);
                }
            }

            var customer = await ResolveCustomerAsync(request);
            var requestedVmName = string.IsNullOrWhiteSpace(request.VmName)
                ? $"PH-VPS-{request.ServiceId}"
                : request.VmName.Trim();
            var vmName = await ResolveUniqueVmNameAsync(requestedVmName, request.ServiceId);

            await db.CreateWhmcsServiceAsync(new WhmcsServiceRecord(
                request.ServiceId,
                request.ClientId,
                customer.User.Id,
                vmName,
                "provisioning",
                string.Empty,
                DateTime.UtcNow,
                DateTime.UtcNow));
            if (customer.Created)
            {
                // Only accounts that WHMCS/PenguinPanel actually created are eligible for
                // automatic orphan cleanup during the final service termination. Existing
                // customer accounts matched by email/username are deliberately never marked.
                await db.MarkWhmcsManagedUserAsync(customer.User.Id, request.ClientId);
            }
            await db.AddAuditAsync("WHMCS_PROVISION_REQUESTED", "whmcs-service", request.ServiceId.ToString(), $"Client {request.ClientId}, user {customer.User.Username}, VM {vmName}.");

            try
            {
                var provision = await panel.ProvisionVmAsync(ApiAdmin, new VmProvisionRequest(
                    customer.User.Id,
                    vmName,
                    request.TemplateId,
                    request.CpuCores,
                    request.MemoryMb,
                    request.DiskGb,
                    request.Generation,
                    request.SecureBoot,
                    request.StartAfterProvision,
                    request.ConfigureWindowsGuest,
                    request.GuestUsername,
                    request.TemplatePassword,
                    request.NewGuestPassword,
                    request.InternalIpAddress,
                    request.RdpExternalPort,
                    request.StorageRoot,
                    request.EnableFiveM,
                    request.FiveMExternalPort,
                    request.FiveMInternalPort));

                var warningText = provision.Warnings.Count == 0 ? string.Empty : string.Join(" | ", provision.Warnings);
                await db.UpdateWhmcsServiceStatusAsync(request.ServiceId, "active", warningText);
                await db.AddAuditAsync("WHMCS_PROVISION_COMPLETED", "whmcs-service", request.ServiceId.ToString(), $"VM {provision.VmName}, warnings {provision.Warnings.Count}.");

                return new WhmcsCreateVpsResponse(
                    request.ServiceId,
                    request.ClientId,
                    customer.User.Id,
                    customer.User.Username,
                    customer.Created,
                    customer.Created ? customer.TemporaryPassword : string.Empty,
                    provision.VmName,
                    "active",
                    provision.State,
                    provision.PublicAddress,
                    provision.RdpExternalPort,
                    provision.InternalIpAddress,
                    provision.StorageRoot,
                    provision.FiveMExternalPort,
                    provision.FiveMInternalPort,
                    provision.GuestUsername,
                    provision.GuestPassword,
                    false,
                    provision.Warnings,
                    provision.Message);
            }
            catch (Exception ex)
            {
                await db.UpdateWhmcsServiceStatusAsync(request.ServiceId, "error", ex.Message);
                await db.AddAuditAsync("WHMCS_PROVISION_FAILED", "whmcs-service", request.ServiceId.ToString(), ex.Message);
                throw;
            }
        }
        finally
        {
            _createLock.Release();
        }
    }

    private async Task<string> ResolveUniqueVmNameAsync(string requestedName, long serviceId)
    {
        var baseName = requestedName.Trim();
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = $"PH-VPS-{serviceId}";
        }

        async Task<bool> IsNameFreeAsync(string name)
        {
            // Hyper-V and the WHMCS mapping table are separate sources of truth.
            // A terminated/older service can still reserve a vm_name in MySQL even
            // when no Hyper-V VM with that name currently exists. Check both before
            // inserting so the ux_whmcs_services_vm_name index never becomes the
            // first place we discover a friendly-name collision.
            if (await panel.GetVmAsync(ApiAdmin, name) is not null)
            {
                return false;
            }

            return !await db.IsWhmcsVmNameInUseAsync(name, serviceId);
        }

        if (await IsNameFreeAsync(baseName))
        {
            return baseName;
        }

        // First names are intentionally friendly but not globally unique. Keep the
        // simple name when possible and append the WHMCS service id on collision.
        var serviceSuffix = $"-{serviceId}";
        var maxBaseLength = Math.Max(1, 100 - serviceSuffix.Length);
        var shortenedBase = baseName.Length > maxBaseLength ? baseName[..maxBaseLength] : baseName;
        var candidate = shortenedBase + serviceSuffix;

        if (await IsNameFreeAsync(candidate))
        {
            await db.AddAuditAsync(
                "WHMCS_VM_NAME_COLLISION",
                "whmcs-service",
                serviceId.ToString(),
                $"Requested VM name '{baseName}' was already reserved in Hyper-V or WHMCS; using '{candidate}'.");
            return candidate;
        }

        // Extremely rare fallback (for example, an old manually-created mapping also
        // used FirstName-ServiceId). Keep trying deterministic suffixes rather than
        // failing on a MySQL duplicate-key error.
        for (var attempt = 2; attempt <= 50; attempt++)
        {
            var suffix = $"-{serviceId}-{attempt}";
            var allowedBaseLength = Math.Max(1, 100 - suffix.Length);
            var safeBase = baseName.Length > allowedBaseLength ? baseName[..allowedBaseLength] : baseName;
            var fallback = safeBase + suffix;
            if (!await IsNameFreeAsync(fallback))
            {
                continue;
            }

            await db.AddAuditAsync(
                "WHMCS_VM_NAME_COLLISION",
                "whmcs-service",
                serviceId.ToString(),
                $"Requested VM names '{baseName}' and '{candidate}' were already reserved; using '{fallback}'.");
            return fallback;
        }

        throw new InvalidOperationException($"Could not allocate a unique VM name based on '{baseName}'. Choose a custom VM name before provisioning.");
    }

    public async Task<WhmcsVpsStatusResponse?> GetStatusAsync(long serviceId)
    {
        var service = await db.GetWhmcsServiceAsync(serviceId);
        return service is null ? null : await BuildStatusAsync(service);
    }

    public async Task<WhmcsCatalogResponse> GetCatalogAsync()
    {
        var templates = (await panel.GetVmTemplatesAsync(ApiAdmin, enabledOnly: true))
            .Select(item => new WhmcsTemplateInfo(item.Id, item.Name, item.OperatingSystem, item.Notes))
            .ToList();
        var defaults = panel.GetVmProvisioningDefaults(ApiAdmin);
        var storageRoots = panel.GetVmStorageRootStatuses(ApiAdmin)
            .Select(item => new WhmcsStorageRootInfo(
                item.Path,
                item.IsDefault,
                item.IsAvailable,
                item.FreeBytes,
                item.TotalBytes,
                Math.Round(item.FreeBytes / 1024d / 1024d / 1024d, 1),
                Math.Round(item.TotalBytes / 1024d / 1024d / 1024d, 1),
                item.Error))
            .ToList();
        var reserveGb = panel.GetMinimumStorageReserveGb(ApiAdmin);
        var reservePercent = panel.GetMinimumStorageReservePercent(ApiAdmin);
        var selectionMode = panel.GetStorageSelectionMode(ApiAdmin);

        return new WhmcsCatalogResponse(
            templates,
            defaults.VmStorageRoots,
            defaults.VmStorageRoot,
            defaults.DefaultGeneration,
            defaults.DefaultSecureBoot,
            defaults.RdpPortStart,
            defaults.RdpPortEnd,
            defaults.FiveMPortStart,
            defaults.FiveMPortEnd,
            defaults.DefaultFiveMInternalPort,
            storageRoots,
            reserveGb,
            reservePercent,
            selectionMode);
    }

    public async Task<WhmcsOperationResponse> SuspendAsync(long serviceId)
    {
        var service = await RequireServiceAsync(serviceId);
        if (string.Equals(service.Status, "terminated", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This WHMCS service is already terminated.");
        }

        var vm = await panel.GetVmAsync(ApiAdmin, service.VmName)
            ?? throw new InvalidOperationException($"VM '{service.VmName}' does not exist on the Hyper-V host.");

        if (!IsSuspendedState(vm.State) && !IsStoppedState(vm.State))
        {
            await panel.VmActionAsync("suspend", service.VmName);
        }

        await db.UpdateWhmcsServiceStatusAsync(serviceId, "suspended", string.Empty);
        await db.AddAuditAsync("WHMCS_SERVICE_SUSPENDED", "whmcs-service", serviceId.ToString(), $"VM {service.VmName}.");
        var refreshed = await panel.GetVmAsync(ApiAdmin, service.VmName);
        return new WhmcsOperationResponse(serviceId, service.VmName, "suspended", refreshed?.State ?? vm.State, "VPS suspended.");
    }

    public async Task<WhmcsOperationResponse> UnsuspendAsync(long serviceId)
    {
        var service = await RequireServiceAsync(serviceId);
        if (string.Equals(service.Status, "terminated", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A terminated WHMCS service cannot be unsuspended.");
        }

        var vm = await panel.GetVmAsync(ApiAdmin, service.VmName)
            ?? throw new InvalidOperationException($"VM '{service.VmName}' does not exist on the Hyper-V host.");

        if (!IsRunningState(vm.State))
        {
            if (IsStoppedState(vm.State))
            {
                await panel.VmActionAsync("start", service.VmName);
            }
            else
            {
                try
                {
                    await panel.VmActionAsync("resume", service.VmName);
                }
                catch
                {
                    await panel.VmActionAsync("start", service.VmName);
                }
            }
        }

        await db.UpdateWhmcsServiceStatusAsync(serviceId, "active", string.Empty);
        await db.AddAuditAsync("WHMCS_SERVICE_UNSUSPENDED", "whmcs-service", serviceId.ToString(), $"VM {service.VmName}.");
        var refreshed = await panel.GetVmAsync(ApiAdmin, service.VmName);
        return new WhmcsOperationResponse(serviceId, service.VmName, "active", refreshed?.State ?? vm.State, "VPS unsuspended.");
    }

    public async Task<WhmcsResizeVpsResponse> ResizeAsync(long serviceId, WhmcsResizeVpsRequest request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("Resize request is required.");
        }
        if (request.CpuCores <= 0)
        {
            throw new InvalidOperationException("cpuCores must be greater than zero.");
        }
        if (request.MemoryMb < 512)
        {
            throw new InvalidOperationException("memoryMb must be at least 512.");
        }
        if (request.DiskGb <= 0)
        {
            throw new InvalidOperationException("diskGb must be greater than zero.");
        }

        var service = await RequireServiceAsync(serviceId);
        if (string.Equals(service.Status, "terminated", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A terminated WHMCS service cannot be resized.");
        }
        if (string.Equals(service.Status, "provisioning", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Wait for provisioning to finish before changing VPS resources.");
        }
        if (string.Equals(service.Status, "error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This WHMCS service is in an error state. Repair the VPS before changing its package.");
        }

        var vm = await panel.GetVmAsync(ApiAdmin, service.VmName)
            ?? throw new InvalidOperationException($"VM '{service.VmName}' does not exist on the Hyper-V host.");

        var resized = await panel.ResizeVmResourcesAsync(
            ApiAdmin,
            service.VmName,
            request.CpuCores,
            request.MemoryMb,
            request.DiskGb);

        var warningText = resized.Warnings.Count == 0 ? string.Empty : string.Join(" | ", resized.Warnings);
        await db.UpdateWhmcsServiceStatusAsync(serviceId, service.Status, warningText);
        await db.AddAuditAsync(
            "WHMCS_PACKAGE_CHANGED",
            "whmcs-service",
            serviceId.ToString(),
            $"VM {service.VmName}: CPU {resized.OldCpuCores}->{resized.NewCpuCores}, RAM {resized.OldMemoryMb}->{resized.NewMemoryMb} MB, disk {resized.OldDiskGb}->{resized.NewDiskGb} GB, warnings {resized.Warnings.Count}.");

        return new WhmcsResizeVpsResponse(
            serviceId,
            service.VmName,
            service.Status,
            resized.FinalState,
            resized.OldCpuCores,
            resized.NewCpuCores,
            resized.OldMemoryMb,
            resized.NewMemoryMb,
            resized.OldDiskGb,
            resized.NewDiskGb,
            resized.Restarted,
            resized.GuestPartitionExpanded,
            resized.Warnings,
            resized.Message);
    }

    public async Task<WhmcsOperationResponse> TerminateAsync(long serviceId)
    {
        var service = await RequireServiceAsync(serviceId);
        if (string.Equals(service.Status, "terminated", StringComparison.OrdinalIgnoreCase))
        {
            return new WhmcsOperationResponse(serviceId, service.VmName, "terminated", "Deleted", "VPS was already terminated.");
        }

        var vm = await panel.GetVmAsync(ApiAdmin, service.VmName);
        if (vm is null)
        {
            throw new InvalidOperationException($"VM '{service.VmName}' is missing. PenguinPanel will not mark the service terminated automatically because stale WinNAT/firewall data may still need cleanup.");
        }

        var result = await panel.DeleteVmAsync(ApiAdmin, service.VmName);
        await db.UpdateWhmcsServiceStatusAsync(serviceId, "terminated", string.Empty);

        var cleanupMessage = string.Empty;
        var user = await db.GetUserByIdAsync(service.PanelUserId);
        var isWhmcsManagedUser = await db.IsWhmcsManagedUserAsync(service.PanelUserId);

        // Step 2.9.8 legacy adoption:
        // Accounts created by older PenguinPanel/WHMCS builds may predate the
        // whmcs_managed_users marker. Never guess based only on service linkage,
        // email, username, or the fact that the account currently has no VMs.
        // Only adopt a legacy account when the audit log proves USER_CREATED was
        // performed by the WHMCS API.
        if (!isWhmcsManagedUser
            && user is not null
            && !user.IsAdmin
            && !user.IsSubUser
            && await db.WasUserCreatedByWhmcsApiAsync(service.PanelUserId, user.Username))
        {
            await db.MarkWhmcsManagedUserAsync(service.PanelUserId, service.ClientId);
            isWhmcsManagedUser = true;

            await db.AddAuditAsync(
                "WHMCS_LEGACY_PANEL_USER_ADOPTED",
                "user",
                user.Username,
                $"Legacy account was proven to have been created by WHMCS API and was marked as WHMCS-managed during termination of service {serviceId}.");
        }

        if (isWhmcsManagedUser)
        {
            var remainingWhmcsServices = await db.CountNonTerminatedWhmcsServicesForUserAsync(service.PanelUserId);
            var remainingVmAssignments = await db.CountUserVmAssignmentsAsync(service.PanelUserId);

            if (remainingWhmcsServices == 0 && remainingVmAssignments == 0)
            {
                user ??= await db.GetUserByIdAsync(service.PanelUserId);
                if (user is not null && !user.IsAdmin && !user.IsSubUser)
                {
                    try
                    {
                        await panel.DeleteUserAsync(ApiAdmin, service.PanelUserId);
                        await db.DeleteWhmcsManagedUserMarkerAsync(service.PanelUserId);
                        cleanupMessage = $" Removed the unused WHMCS-created PenguinPanel account '{user.Username}'.";
                        await db.AddAuditAsync(
                            "WHMCS_PANEL_USER_DELETED",
                            "user",
                            user.Username,
                            $"Removed after terminating final WHMCS service {serviceId}.");
                    }
                    catch (Exception ex)
                    {
                        // The VPS termination already succeeded. Do not turn that successful
                        // destructive operation into a failed WHMCS command just because the
                        // optional orphan-user cleanup could not complete. Keep the marker so
                        // an administrator can safely retry/clean it up later.
                        cleanupMessage = " Warning: the VPS was deleted, but the unused PenguinPanel account could not be removed automatically: " + ex.Message;
                        await db.AddAuditAsync(
                            "WHMCS_PANEL_USER_DELETE_FAILED",
                            "user",
                            service.PanelUserId.ToString(),
                            ex.Message);
                    }
                }
            }
        }

        var finalMessage = result.Message + cleanupMessage;
        await db.AddAuditAsync("WHMCS_SERVICE_TERMINATED", "whmcs-service", serviceId.ToString(), finalMessage);
        return new WhmcsOperationResponse(serviceId, service.VmName, "terminated", "Deleted", finalMessage);
    }

    private async Task<CustomerResolution> ResolveCustomerAsync(WhmcsCreateVpsRequest request)
    {
        if (request.PanelUserId is > 0)
        {
            var explicitUser = await db.GetUserByIdAsync(request.PanelUserId.Value)
                ?? throw new InvalidOperationException("The requested PenguinPanel user does not exist.");
            if (explicitUser.IsAdmin || explicitUser.IsSubUser)
            {
                throw new InvalidOperationException("WHMCS VPS services must be linked to a main customer account, not an admin or sub-user.");
            }
            await SyncWhmcsCustomerEmailAsync(explicitUser.Id, request.CustomerEmail);
            return new CustomerResolution(explicitUser, false, string.Empty);
        }

        var linkedUserId = await db.GetWhmcsPanelUserIdForClientAsync(request.ClientId);
        if (linkedUserId is > 0)
        {
            var linked = await db.GetUserByIdAsync(linkedUserId.Value);
            if (linked is not null && !linked.IsAdmin && !linked.IsSubUser)
            {
                await SyncWhmcsCustomerEmailAsync(linked.Id, request.CustomerEmail);
                return new CustomerResolution(linked, false, string.Empty);
            }
        }

        var email = (request.CustomerEmail ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(email))
        {
            var byEmail = await db.GetUserByEmailAsync(email);
            if (byEmail is not null && !byEmail.IsAdmin && !byEmail.IsSubUser)
            {
                await SyncWhmcsCustomerEmailAsync(byEmail.Id, request.CustomerEmail);
                return new CustomerResolution(byEmail, false, string.Empty);
            }
        }

        var requestedUsername = NormalizeCustomerUsername(request.CustomerUsername, request.ClientId);
        var byUsername = await db.GetUserByUsernameAsync(requestedUsername);
        if (byUsername is not null)
        {
            if (byUsername.IsAdmin || byUsername.IsSubUser)
            {
                throw new InvalidOperationException($"PenguinPanel username '{requestedUsername}' belongs to an admin or sub-user and cannot be linked to WHMCS client {request.ClientId}.");
            }
            await SyncWhmcsCustomerEmailAsync(byUsername.Id, request.CustomerEmail);
            return new CustomerResolution(byUsername, false, string.Empty);
        }

        var username = await FindAvailableUsernameAsync(requestedUsername, request.ClientId);
        var temporaryPassword = string.IsNullOrWhiteSpace(request.PanelTemporaryPassword)
            ? GeneratePassword()
            : request.PanelTemporaryPassword;

        await panel.CreateUserAsync(ApiAdmin, username, temporaryPassword, "user", email, UserVmPermissions.ClientDefault, mustChangePassword: false);
        var createdUser = await db.GetUserByUsernameAsync(username)
            ?? throw new InvalidOperationException("PenguinPanel created the customer account but could not read it back from the database.");

        return new CustomerResolution(createdUser, true, temporaryPassword);
    }

    private async Task SyncWhmcsCustomerEmailAsync(long panelUserId, string? customerEmail)
    {
        var value = (customerEmail ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value) || !MailAddress.TryCreate(value, out var parsed))
        {
            return;
        }

        var user = await db.GetUserByIdAsync(panelUserId);
        if (user is null || user.IsAdmin || user.IsSubUser)
        {
            return;
        }

        // WHMCS is authoritative for the primary customer's delivery address.
        // Keeping the linked panel account synchronized makes monitoring mail
        // work even when the panel user was originally created without email.
        await db.UpdateUserEmailAsync(user.Id, parsed.Address);
    }

    private async Task<string> FindAvailableUsernameAsync(string baseUsername, long clientId)
    {
        if (await db.GetUserByUsernameAsync(baseUsername) is null)
        {
            return baseUsername;
        }

        var candidate = $"{baseUsername}-{clientId}";
        if (await db.GetUserByUsernameAsync(candidate) is null)
        {
            return candidate;
        }

        for (var i = 2; i <= 100; i++)
        {
            candidate = $"{baseUsername}-{clientId}-{i}";
            if (await db.GetUserByUsernameAsync(candidate) is null)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not generate a unique PenguinPanel username for this WHMCS client.");
    }

    private async Task<WhmcsCreateVpsResponse> BuildExistingCreateResponseAsync(WhmcsServiceRecord service)
    {
        var user = await db.GetUserByIdAsync(service.PanelUserId);
        var status = await BuildStatusAsync(service);
        return new WhmcsCreateVpsResponse(
            service.ServiceId,
            service.ClientId,
            service.PanelUserId,
            user?.Username ?? string.Empty,
            false,
            string.Empty,
            service.VmName,
            service.Status,
            status.VmState,
            status.PublicAddress,
            status.RdpPort ?? 0,
            status.InternalIpAddress,
            string.Empty,
            status.FiveMPort,
            status.FiveMInternalPort,
            string.Empty,
            string.Empty,
            true,
            string.IsNullOrWhiteSpace(service.LastError) ? [] : [service.LastError],
            string.Equals(service.Status, "error", StringComparison.OrdinalIgnoreCase)
                ? "This WHMCS service already exists in an error state. Review/clean up the existing VM before retrying provisioning."
                : string.Equals(service.Status, "terminated", StringComparison.OrdinalIgnoreCase)
                    ? "This WHMCS service is marked terminated, but its old Hyper-V VM still exists. PenguinPanel refused to recreate it to avoid a duplicate VPS. Remove/reconcile the stale VM first."
                    : "This WHMCS service already exists. No duplicate VPS was created.");
    }

    private async Task<WhmcsVpsStatusResponse> BuildStatusAsync(WhmcsServiceRecord service)
    {
        var vm = await panel.GetVmAsync(ApiAdmin, service.VmName);
        var access = await db.GetVmAccessInfoAsync(service.VmName);
        var profile = await db.GetVmNetworkPortProfileAsync(service.VmName);
        var publicAddress = !string.IsNullOrWhiteSpace(access.Hostname)
            ? access.Hostname
            : !string.IsNullOrWhiteSpace(profile.PublicHostname)
                ? profile.PublicHostname
                : !string.IsNullOrWhiteSpace(profile.PublicIpAddress)
                    ? profile.PublicIpAddress
                    : access.IpAddress;

        return new WhmcsVpsStatusResponse(
            service.ServiceId,
            service.ClientId,
            service.PanelUserId,
            service.VmName,
            service.Status,
            vm is not null,
            vm?.State ?? "Missing",
            publicAddress ?? string.Empty,
            profile.RdpExternalPort ?? access.RdpPort,
            profile.InternalIpAddress ?? string.Empty,
            profile.EnableFiveM ? profile.FiveMExternalPort : null,
            profile.EnableFiveM ? profile.FiveMInternalPort : null,
            service.LastError,
            service.UpdatedAt);
    }

    private async Task<WhmcsServiceRecord> RequireServiceAsync(long serviceId)
    {
        if (serviceId <= 0)
        {
            throw new InvalidOperationException("WHMCS service id must be greater than zero.");
        }

        return await db.GetWhmcsServiceAsync(serviceId)
            ?? throw new KeyNotFoundException($"WHMCS service {serviceId} was not found.");
    }

    private static void ValidateCreateRequest(WhmcsCreateVpsRequest request)
    {
        if (request.ServiceId <= 0) throw new InvalidOperationException("serviceId must be greater than zero.");
        if (request.ClientId <= 0) throw new InvalidOperationException("clientId must be greater than zero.");
        if (request.TemplateId <= 0) throw new InvalidOperationException("templateId must be greater than zero.");
        if (request.CpuCores <= 0) throw new InvalidOperationException("cpuCores must be greater than zero.");
        if (request.MemoryMb < 512) throw new InvalidOperationException("memoryMb must be at least 512.");
        if (request.DiskGb <= 0) throw new InvalidOperationException("diskGb must be greater than zero.");
        if (request.ConfigureWindowsGuest && string.IsNullOrWhiteSpace(request.TemplatePassword))
        {
            throw new InvalidOperationException("templatePassword is required when configureWindowsGuest is true.");
        }
    }

    private static string NormalizeCustomerUsername(string value, long clientId)
    {
        var input = (value ?? string.Empty).Trim();

        // Do not use a customer's email address as their panel username. It is
        // unnecessary PII in logs/screenshots and can leak into future naming
        // conventions. The WHMCS module normally sends penguin-<clientId>, but
        // enforce the privacy-safe fallback here too so older/custom modules
        // cannot accidentally create email-address usernames.
        if (string.IsNullOrWhiteSpace(input) || input.Contains('@'))
        {
            input = $"penguin-{clientId}";
        }

        var cleaned = new string(input.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-').ToArray()).Trim('-');
        if (cleaned.Length < 3)
        {
            cleaned = $"penguin-{clientId}";
        }

        return cleaned.Length <= 120 ? cleaned : cleaned[..120];
    }

    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@$%";
        var bytes = RandomNumberGenerator.GetBytes(20);
        return new string(bytes.Select(item => alphabet[item % alphabet.Length]).ToArray());
    }

    private static bool IsRunningState(string state) =>
        state.Contains("Running", StringComparison.OrdinalIgnoreCase) || state == "2";

    private static bool IsStoppedState(string state) =>
        state.Contains("Off", StringComparison.OrdinalIgnoreCase) || state.Contains("Stopped", StringComparison.OrdinalIgnoreCase) || state == "3";

    private static bool IsSuspendedState(string state) =>
        state.Contains("Paused", StringComparison.OrdinalIgnoreCase)
        || state.Contains("Saved", StringComparison.OrdinalIgnoreCase)
        || state.Contains("Suspended", StringComparison.OrdinalIgnoreCase)
        || state.Contains("Pausing", StringComparison.OrdinalIgnoreCase)
        || state == "32768"
        || state == "32769";

    private sealed record CustomerResolution(PanelUser User, bool Created, string TemporaryPassword);
}
