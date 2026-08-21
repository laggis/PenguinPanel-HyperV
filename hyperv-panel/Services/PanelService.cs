namespace HyperVPanel.Services;

public sealed class PanelService(AppDatabase db, PowerShellService powerShell, EmailService email, IConfiguration configuration)
{
    public bool IsSmtpEnabled => email.IsEnabled;
    private const string AbuseMeterSampleScope = "meter";
    private readonly object _abuseStateLock = new();
    private readonly Dictionary<string, AbuseDetectionState> _abuseStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EmergencyResetCodeState> _emergencyResetCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _provisioningLock = new(1, 1);
    private string PanelPublicUrl => NormalizePanelUrl(configuration["Panel:PublicUrl"] ?? "https://vps.penguinhosting.host/");
    private bool EmergencyResetCodeRequired => configuration.GetValue("Security:EmergencyResetRequireEmailCode", true);
    private int EmergencyResetCodeMinutes => Math.Clamp(configuration.GetValue("Security:EmergencyResetCodeMinutes", 10), 2, 60);
    private int EmergencyResetCodeCooldownSeconds => Math.Clamp(configuration.GetValue("Security:EmergencyResetCodeCooldownSeconds", 60), 15, 600);
    public Task<HostNodeStatus> GetHostNodeStatusAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return powerShell.GetHostNodeStatusAsync();
    }

    public Task<PanelUser?> LoginAsync(string username, string password) => db.ValidateLoginAsync(username, password);

    public async Task<bool> RequestPasswordRecoveryAsync(string username)
    {
        username = (username ?? string.Empty).Trim();
        if (username.Length < 3)
        {
            throw new InvalidOperationException("Enter your username before requesting password recovery.");
        }

        var target = await db.GetPasswordRecoveryUserAsync(username);
        var auditResource = target?.Username ?? username;
        var auditDetails = target is null
            ? "No matching user was found for this recovery request."
            : string.IsNullOrWhiteSpace(target.Email)
                ? "Matching user has no email address."
                : $"Matching user email: {target.Email}";

        await db.AddAuditAsync("PASSWORD_RECOVERY_REQUESTED", "user", auditResource, auditDetails);

        if (target is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(target.Email))
        {
            return false;
        }

        var temporaryPassword = GenerateFriendlyPassword();
        await db.UpdateUserPasswordAsync(target.Id, temporaryPassword, mustChangePassword: true);
        await db.RevokeUserSessionsAsync(target.Id);
        await db.AddAuditAsync("PASSWORD_RECOVERY_TEMP_PASSWORD_SENT", "user", target.Username);

        var panelUrl = PanelPublicUrl;
        var body = $"Hello {target.Username},\n\nA temporary PenguinPanel password was generated for your account.\n\nPanel URL: {panelUrl}\nPanel username: {target.Username}\nTemporary password: {temporaryPassword}\n\nSign in with this temporary password. You will be asked to choose a new password before using the panel.";
        return await TrySendEmailAsync(target.Email,
            "PenguinPanel password recovery",
            body,
            BuildPanelEmailHtml(
                "Password recovery",
                "Your temporary password is ready",
                $"Hello {target.Username}, use this temporary password to sign in to PenguinPanel.",
                [
                    new("Panel username", target.Username),
                    new("Temporary password", temporaryPassword, true)
                ],
                "This is a temporary password. After signing in, you must choose a new password before using the panel.",
                "Open PenguinPanel",
                panelUrl));
    }

    public Task<string> CreateSessionTokenAsync(PanelUser user) => db.CreateUserSessionAsync(user.Id);

    public Task<PanelUser?> ValidateSessionTokenAsync(string token) => db.ValidateUserSessionAsync(token);

    public Task RevokeSessionTokenAsync(string token) => db.RevokeUserSessionAsync(token);

    public async Task<IReadOnlyList<VmInfo>> GetVmsAsync(PanelUser? user)
    {
        if (user is null)
        {
            return [];
        }

        var allVms = await ReadAllVmsAsync();
        if (user.IsAdmin)
        {
            return allVms;
        }

        var assigned = await db.GetAssignedVmNamesAsync(user.Id);
        return allVms
            .Where(vm => assigned.Contains(vm.Name))
            .ToList();
    }

    public Task<IReadOnlyList<VmInfo>> GetVmsAsync() => ReadAllVmsAsync();

    public async Task<VmInfo?> GetVmAsync(PanelUser? user, string vmName)
    {
        await EnsureCanAccessVmAsync(user, vmName);
        return await powerShell.GetVmAsync(vmName);
    }

    public async Task VmActionAsync(PanelUser? user, string action, string vmName)
    {
        await EnsureCanAccessVmAsync(user, vmName);
        RequireUserPermission(user, user!.EffectivePermissions.CanUsePowerActions, "Power controls are not enabled for your account.");
        await EnforcePowerActionLimitAsync(user!, vmName, action);
        await RunVmActionAsync(action, vmName);
        await db.AddPowerActionAsync(user!.Id, vmName, action);
        await db.AddAuditAsync($"VM_{action.ToUpperInvariant()}", "vm", vmName, $"By {user!.Username}");

        if (action is "stop" or "restart" or "suspend")
        {
            await NotifyAssignedUsersAsync(vmName, $"PenguinPanel: {vmName} {action}", $"The virtual server {vmName} received action: {action}.\n\nTriggered by: {user.Username}");
        }
    }

    public async Task VmActionAsync(string action, string vmName)
    {
        await RunVmActionAsync(action, vmName);
        await db.AddAuditAsync($"VM_{action.ToUpperInvariant()}", "vm", vmName);
    }

    public async Task<IReadOnlyList<PanelUserSummary>> GetUsersAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await db.GetUsersAsync();
    }

    public async Task<IReadOnlyList<UserVmAssignment>> GetUserVmAssignmentsAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await db.GetUserVmAssignmentsAsync();
    }

    public async Task<bool> CreateUserAsync(PanelUser? currentUser, string username, string password, string role, string emailAddress = "", UserVmPermissions? permissions = null, bool mustChangePassword = true)
    {
        RequireAdmin(currentUser);

        username = username.Trim();
        role = NormalizeRole(role);
        emailAddress = (emailAddress ?? string.Empty).Trim();
        permissions ??= UserVmPermissions.ClientDefault;
        if (role == "admin")
        {
            permissions = UserVmPermissions.Admin;
        }

        ValidateUsername(username);
        ValidatePassword(password);

        var id = await db.CreateUserAsync(username, password, role, emailAddress, permissions, mustChangePassword: mustChangePassword);
        await db.AddAuditAsync("USER_CREATED", "user", username, $"Role: {role}, id: {id}, by {currentUser!.Username}");

        var panelUrl = PanelPublicUrl;
        var passwordLabel = mustChangePassword ? "Temporary password" : "Password";
        var passwordNote = mustChangePassword
            ? "This is a temporary password. Sign in with it, then choose a new password before using the panel."
            : "This account was created by the billing system. You can use this password to sign in to PenguinPanel and change it later from your account settings if you want.";
        var body = $"Hello {username},\n\nYour PenguinPanel account has been created.\n\nPanel URL: {panelUrl}\nPanel username: {username}\n{passwordLabel}: {password}\n\n{passwordNote}";
        var emailSent = await TrySendEmailAsync(emailAddress,
            "PenguinPanel account created",
            body,
            BuildPanelEmailHtml(
                "Account created",
                "Welcome to PenguinPanel",
                $"Hello {username}, your panel account is ready.",
                [
                    new("Panel username", username),
                    new(passwordLabel, password, true)
                ],
                passwordNote,
                "Open PenguinPanel",
                panelUrl));

        return emailSent;
    }

    public async Task<bool> ResetUserPasswordAsync(PanelUser? currentUser, long userId, string password)
    {
        RequireAdmin(currentUser);
        ValidatePassword(password);

        var target = await db.GetUserByIdAsync(userId)
            ?? throw new InvalidOperationException("User was not found.");

        await db.UpdateUserPasswordAsync(userId, password, mustChangePassword: true);
        await db.RevokeUserSessionsAsync(userId);
        await db.AddAuditAsync("USER_PASSWORD_RESET", "user", target.Username, $"By {currentUser!.Username}");

        var panelUrl = PanelPublicUrl;
        var body = $"Hello {target.Username},\n\nYour PenguinPanel password was reset by an administrator.\n\nPanel URL: {panelUrl}\nPanel username: {target.Username}\nTemporary password: {password}\n\nThis is a temporary password. Sign in with it, then choose a new password before using the panel.";
        var emailSent = await TrySendEmailAsync(await db.GetUserEmailAsync(userId),
            "PenguinPanel password reset",
            body,
            BuildPanelEmailHtml(
                "Password reset",
                "Your panel password was reset",
                $"Hello {target.Username}, an administrator reset your PenguinPanel password.",
                [
                    new("Panel username", target.Username),
                    new("Temporary password", password, true)
                ],
                "This is a temporary password. Your old saved browser sessions were signed out. After signing in, you must choose a new password.",
                "Open PenguinPanel",
                panelUrl));

        return emailSent;
    }

    public async Task UpdateUserEmailAsync(PanelUser? currentUser, long userId, string emailAddress)
    {
        RequireAdmin(currentUser);
        var target = await db.GetUserByIdAsync(userId)
            ?? throw new InvalidOperationException("User was not found.");

        await db.UpdateUserEmailAsync(userId, (emailAddress ?? string.Empty).Trim());
        await db.AddAuditAsync("USER_EMAIL_UPDATED", "user", target.Username, $"By {currentUser!.Username}");
    }

    public async Task UpdateUserRoleAsync(PanelUser? currentUser, long userId, string role)
    {
        RequireAdmin(currentUser);
        role = NormalizeRole(role);

        var target = await db.GetUserByIdAsync(userId)
            ?? throw new InvalidOperationException("User was not found.");

        if (string.Equals(target.Username, "admin", StringComparison.OrdinalIgnoreCase) && role != "admin")
        {
            throw new InvalidOperationException("The built-in admin account must stay admin.");
        }

        if (target.Id == currentUser!.Id && role != "admin")
        {
            throw new InvalidOperationException("You cannot remove admin from the account you are currently signed in with.");
        }

        if (target.IsAdmin && role != "admin" && await db.CountAdminsAsync() <= 1)
        {
            throw new InvalidOperationException("You must keep at least one admin account.");
        }

        await db.UpdateUserRoleAsync(userId, role);
        if (role == "admin")
        {
            await db.UpdateUserPermissionsAsync(userId, UserVmPermissions.Admin);
        }
        else if (target.IsAdmin)
        {
            await db.UpdateUserPermissionsAsync(userId, UserVmPermissions.ClientDefault);
        }
        await db.AddAuditAsync("USER_ROLE_UPDATED", "user", target.Username, $"Role: {role}, by {currentUser.Username}");
    }

    public async Task UpdateUserPermissionsAsync(PanelUser? currentUser, long userId, UserVmPermissions permissions)
    {
        RequireAdmin(currentUser);

        var target = await db.GetUserByIdAsync(userId)
            ?? throw new InvalidOperationException("User was not found.");

        if (target.IsAdmin)
        {
            permissions = UserVmPermissions.Admin;
        }

        await db.UpdateUserPermissionsAsync(userId, permissions);
        await db.AddAuditAsync("USER_PERMISSIONS_UPDATED", "user", target.Username, $"By {currentUser!.Username}");
    }

    public async Task<string> GetOwnEmailAsync(PanelUser? currentUser)
    {
        RequireSignedIn(currentUser);
        return await db.GetUserEmailAsync(currentUser!.Id);
    }

    public async Task UpdateOwnEmailAsync(PanelUser? currentUser, string emailAddress)
    {
        RequireSignedIn(currentUser);
        emailAddress = NormalizeEmail(emailAddress);
        await db.UpdateUserEmailAsync(currentUser!.Id, emailAddress);
        await db.AddAuditAsync("USER_OWN_EMAIL_UPDATED", "user", currentUser.Username, "By account owner");
    }

    public async Task ChangeOwnPasswordAsync(PanelUser? currentUser, string currentPassword, string newPassword)
    {
        RequireSignedIn(currentUser);
        ValidatePassword(newPassword);

        var verified = await db.ValidateLoginAsync(currentUser!.Username, currentPassword);
        if (verified is null)
        {
            throw new InvalidOperationException("Current password is incorrect.");
        }

        await db.UpdateUserPasswordAsync(currentUser.Id, newPassword);
        await db.AddAuditAsync("USER_OWN_PASSWORD_CHANGED", "user", currentUser.Username);
    }

    public async Task<PanelUser> CompleteRequiredPasswordChangeAsync(PanelUser? currentUser, string newPassword)
    {
        RequireSignedIn(currentUser);
        ValidatePassword(newPassword);

        await db.UpdateUserPasswordAsync(currentUser!.Id, newPassword, mustChangePassword: false);
        await db.AddAuditAsync("USER_REQUIRED_PASSWORD_CHANGED", "user", currentUser.Username);
        return await db.GetUserByIdAsync(currentUser.Id)
            ?? currentUser with { MustChangePassword = false };
    }

    public async Task<IReadOnlyList<PanelUserSummary>> GetClientSubUsersAsync(PanelUser? currentUser)
    {
        RequireClientOwner(currentUser);
        return await db.GetSubUsersAsync(currentUser!.Id);
    }

    public async Task<IReadOnlyList<UserVmAssignment>> GetClientSubUserVmAssignmentsAsync(PanelUser? currentUser)
    {
        RequireClientOwner(currentUser);
        var subUserIds = (await db.GetSubUsersAsync(currentUser!.Id)).Select(item => item.Id).ToHashSet();
        return (await db.GetUserVmAssignmentsAsync())
            .Where(item => subUserIds.Contains(item.UserId))
            .ToList();
    }

    public async Task<bool> CreateClientSubUserAsync(PanelUser? currentUser, string username, string password, string emailAddress, UserVmPermissions permissions, IReadOnlyList<string> vmNames)
    {
        RequireClientOwner(currentUser);

        username = username.Trim();
        emailAddress = NormalizeEmail(emailAddress);
        ValidateUsername(username);
        ValidatePassword(password);

        var cappedPermissions = CapPermissions(permissions, currentUser!.EffectivePermissions);
        var allowedVmNames = await ValidateClientVmSelectionAsync(currentUser, vmNames);
        var id = await db.CreateUserAsync(username, password, "user", emailAddress, cappedPermissions, currentUser.Id);
        await db.SetUserVmAssignmentsAsync(id, allowedVmNames);
        await db.AddAuditAsync("CLIENT_SUBUSER_CREATED", "user", username, $"By {currentUser.Username}");

        return await TrySendEmailAsync(emailAddress,
            "PenguinPanel sub-user account created",
            $"Hello {username},\n\nA PenguinPanel sub-user account has been created for you by {currentUser.Username}.\n\nPanel URL: {PanelPublicUrl}\nPanel username: {username}\nTemporary password: {password}\n\nThis is a temporary password. Sign in with it, then choose a new password before using the panel.",
            BuildPanelEmailHtml(
                "Sub-user created",
                "Your PenguinPanel access is ready",
                $"Hello {username}, {currentUser.Username} created a sub-user account for you.",
                [
                    new("Panel username", username),
                    new("Temporary password", password, true)
                ],
                "This is a temporary password. After signing in, you must choose a new password before using the panel. Your access is limited to the virtual servers and permissions selected by the account owner.",
                "Open PenguinPanel",
                PanelPublicUrl));
    }

    public async Task UpdateClientSubUserEmailAsync(PanelUser? currentUser, long subUserId, string emailAddress)
    {
        var target = await RequireOwnedSubUserAsync(currentUser, subUserId);
        emailAddress = NormalizeEmail(emailAddress);
        await db.UpdateUserEmailAsync(subUserId, emailAddress);
        await db.AddAuditAsync("CLIENT_SUBUSER_EMAIL_UPDATED", "user", target.Username, $"By {currentUser!.Username}");
    }

    public async Task<bool> ResetClientSubUserPasswordAsync(PanelUser? currentUser, long subUserId, string password)
    {
        var target = await RequireOwnedSubUserAsync(currentUser, subUserId);
        ValidatePassword(password);
        await db.UpdateUserPasswordAsync(subUserId, password, mustChangePassword: true);
        await db.RevokeUserSessionsAsync(subUserId);
        await db.AddAuditAsync("CLIENT_SUBUSER_PASSWORD_RESET", "user", target.Username, $"By {currentUser!.Username}");

        return await TrySendEmailAsync(await db.GetUserEmailAsync(subUserId),
            "PenguinPanel sub-user password reset",
            $"Hello {target.Username},\n\nYour PenguinPanel sub-user password was reset by {currentUser.Username}.\n\nPanel URL: {PanelPublicUrl}\nPanel username: {target.Username}\nTemporary password: {password}\n\nThis is a temporary password. Sign in with it, then choose a new password before using the panel.",
            BuildPanelEmailHtml(
                "Password reset",
                "Your sub-user password was reset",
                $"Hello {target.Username}, {currentUser.Username} reset your PenguinPanel password.",
                [
                    new("Panel username", target.Username),
                    new("Temporary password", password, true)
                ],
                "This is a temporary password. Your old saved browser sessions were signed out. After signing in, you must choose a new password.",
                "Open PenguinPanel",
                PanelPublicUrl));
    }

    public async Task UpdateClientSubUserPermissionsAsync(PanelUser? currentUser, long subUserId, UserVmPermissions permissions)
    {
        var target = await RequireOwnedSubUserAsync(currentUser, subUserId);
        var cappedPermissions = CapPermissions(permissions, currentUser!.EffectivePermissions);
        await db.UpdateUserPermissionsAsync(subUserId, cappedPermissions);
        await db.AddAuditAsync("CLIENT_SUBUSER_PERMISSIONS_UPDATED", "user", target.Username, $"By {currentUser.Username}");
    }

    public async Task SetClientSubUserVmAssignmentsAsync(PanelUser? currentUser, long subUserId, IReadOnlyList<string> vmNames)
    {
        var target = await RequireOwnedSubUserAsync(currentUser, subUserId);
        var allowedVmNames = await ValidateClientVmSelectionAsync(currentUser!, vmNames);
        await db.SetUserVmAssignmentsAsync(subUserId, allowedVmNames);
        await db.AddAuditAsync("CLIENT_SUBUSER_VMS_UPDATED", "user", target.Username, $"By {currentUser!.Username}: {string.Join(", ", allowedVmNames)}");
    }

    public async Task DeleteClientSubUserAsync(PanelUser? currentUser, long subUserId)
    {
        var target = await RequireOwnedSubUserAsync(currentUser, subUserId);
        await db.DeleteUserAsync(subUserId);
        await db.AddAuditAsync("CLIENT_SUBUSER_DELETED", "user", target.Username, $"By {currentUser!.Username}");
    }

    public async Task DeleteUserAsync(PanelUser? currentUser, long userId)
    {
        RequireAdmin(currentUser);
        var target = await db.GetUserByIdAsync(userId)
            ?? throw new InvalidOperationException("User was not found.");

        if (target.Id == currentUser!.Id)
        {
            throw new InvalidOperationException("You cannot delete the account you are currently signed in with.");
        }

        if (string.Equals(target.Username, "admin", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The built-in admin account cannot be deleted.");
        }

        if (target.IsAdmin && await db.CountAdminsAsync() <= 1)
        {
            throw new InvalidOperationException("You must keep at least one admin account.");
        }

        await db.DeleteUserAsync(userId);
        await db.AddAuditAsync("USER_DELETED", "user", target.Username, $"By {currentUser.Username}");
    }

    public async Task AssignVmToUserAsync(PanelUser? currentUser, long userId, string vmName)
    {
        RequireAdmin(currentUser);

        if (string.IsNullOrWhiteSpace(vmName))
        {
            throw new InvalidOperationException("Select a VM first.");
        }

        var target = await db.GetUserByIdAsync(userId)
            ?? throw new InvalidOperationException("User was not found.");

        await EnforceVmAssignmentLimitAsync(target, vmName.Trim());
        await db.AssignVmToUserAsync(userId, vmName.Trim());
        await db.AddAuditAsync("VM_ASSIGNED", "vm", vmName, $"Assigned to {target.Username} by {currentUser!.Username}");

        await TrySendEmailAsync(await db.GetUserEmailAsync(userId),
            $"PenguinPanel: VM assigned - {vmName}",
            $"Hello {target.Username},\n\nA virtual server has been assigned to your account.\n\nVM: {vmName}\n\nSign in to PenguinPanel to manage it.");
    }

    public async Task UnassignVmFromUserAsync(PanelUser? currentUser, long assignmentId)
    {
        RequireAdmin(currentUser);

        var assignments = await db.GetUserVmAssignmentsAsync();
        var target = assignments.FirstOrDefault(item => item.Id == assignmentId)
            ?? throw new InvalidOperationException("Assignment was not found.");

        await db.UnassignVmFromUserAsync(assignmentId);
        await db.AddAuditAsync("VM_UNASSIGNED", "vm", target.VmName, $"Removed from {target.Username} by {currentUser!.Username}");
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogsAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await db.GetAuditLogsAsync();
    }

    public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogsAsync() => db.GetAuditLogsAsync();

    public async Task DeleteAuditLogAsync(PanelUser? currentUser, long logId)
    {
        RequireAdmin(currentUser);
        await db.DeleteAuditLogAsync(logId);
    }

    public async Task<int> ClearAuditLogsAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await db.ClearAuditLogsAsync();
    }

    public async Task<int> DeleteOldAuditLogsAsync(PanelUser? currentUser, int olderThanDays)
    {
        RequireAdmin(currentUser);
        return await db.DeleteOldAuditLogsAsync(olderThanDays);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogsForVmAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewActivity, "Activity logs are not enabled for your account.");
        var logs = await db.GetAuditLogsAsync();
        return logs.Where(item => string.Equals(item.ResourceId, vmName, StringComparison.OrdinalIgnoreCase)).Take(25).ToList();
    }

    public Task<AbuseConfig> GetAbuseConfigAsync() => db.GetAbuseConfigAsync();

    public Task SaveAbuseConfigAsync(AbuseConfig config) => db.SaveAbuseConfigAsync(config);

    public Task<IReadOnlyList<AbuseEvent>> GetAbuseEventsAsync() => db.GetAbuseEventsAsync();

    public async Task DeleteAbuseEventAsync(PanelUser? currentUser, long eventId)
    {
        RequireAdmin(currentUser);
        await db.DeleteAbuseEventAsync(eventId);
    }

    public async Task<int> ClearAbuseEventsAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await db.ClearAbuseEventsAsync();
    }

    public async Task<(int EventsDeleted, int SamplesDeleted)> DeleteOldAbuseDataAsync(PanelUser? currentUser, int olderThanDays)
    {
        RequireAdmin(currentUser);
        return await db.DeleteOldAbuseDataAsync(olderThanDays);
    }

    public async Task<VmAccessInfo> GetVmAccessInfoAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        return await db.GetVmAccessInfoAsync(vmName);
    }

    public async Task SaveVmAccessInfoAsync(PanelUser? currentUser, VmAccessInfo info)
    {
        RequireAdmin(currentUser);
        await db.SaveVmAccessInfoAsync(info);
        await db.AddAuditAsync("VM_ACCESS_UPDATED", "vm", info.VmName, $"By {currentUser!.Username}");
    }

    public async Task<VmRdpListenerStatus> GetVmRdpListenerStatusAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewAccess, "Access details are not enabled for your account.");
        var access = await db.GetVmAccessInfoAsync(vmName);
        if (string.IsNullOrWhiteSpace(access.Username) || string.IsNullOrWhiteSpace(access.Password))
        {
            throw new InvalidOperationException("Save the Windows username and password on the Access tab before verifying the RDP listener.");
        }

        return await powerShell.GetWindowsGuestRdpListenerStatusAsync(vmName, access.Username, access.Password);
    }

    public async Task<VmRdpListenerStatus> SyncVmWindowsRdpPortToPublicAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireAdmin(currentUser);

        var access = await db.GetVmAccessInfoAsync(vmName);
        if (string.IsNullOrWhiteSpace(access.Username) || string.IsNullOrWhiteSpace(access.Password))
        {
            throw new InvalidOperationException("Save the Windows username and password on the Access tab before changing the Windows RDP listener.");
        }

        var profile = NormalizeNetworkProfile(await db.GetVmNetworkPortProfileAsync(vmName));
        var targetPort = profile.RdpExternalPort is > 0
            ? profile.RdpExternalPort.Value
            : access.RdpPort is > 0
                ? access.RdpPort.Value
                : 3389;

        var previousInternalPort = profile.RdpInternalPort > 0 ? profile.RdpInternalPort : 3389;
        var updatedProfile = profile with
        {
            EnableRdp = true,
            RdpExternalPort = targetPort,
            RdpInternalPort = targetPort,
            UpdatedAt = DateTime.UtcNow
        };

        var conflicts = await GetNetworkProfileConflictsAsync(updatedProfile);
        if (conflicts.Count > 0)
        {
            throw new InvalidOperationException("Port conflict: " + string.Join("; ", conflicts.Select(item => $"{item.Protocol}/{item.ExternalPort} is already used by {item.ConflictVmName}")));
        }

        // PowerShell Direct does not depend on the VM's network. Change the Windows listener
        // first, then immediately move WinNAT to the same internal port.
        var status = await powerShell.SetWindowsGuestRdpPortAsync(vmName, access.Username, access.Password, targetPort);
        try
        {
            await db.SaveVmNetworkPortProfileAsync(updatedProfile);
            await powerShell.ApplyNetworkPortProfileAsync(updatedProfile);
            await powerShell.SyncWindowsGuestFirewallRulesAsync(updatedProfile, access.Username, access.Password);
            await UpdateAccessFromNetworkProfileAsync(updatedProfile);
        }
        catch
        {
            // Best-effort rollback. If host mapping/profile update fails after Windows changed,
            // restore the guest listener, saved profile, guest firewall, and WinNAT target.
            try
            {
                if (previousInternalPort != targetPort)
                {
                    await powerShell.SetWindowsGuestRdpPortAsync(vmName, access.Username, access.Password, previousInternalPort);
                }
                await db.SaveVmNetworkPortProfileAsync(profile);
                await powerShell.ApplyNetworkPortProfileAsync(profile);
                await powerShell.SyncWindowsGuestFirewallRulesAsync(profile, access.Username, access.Password);
                await UpdateAccessFromNetworkProfileAsync(profile);
            }
            catch
            {
                // Preserve the original failure; an administrator can use the Network and Access
                // tabs to inspect/recover the mapping if this best-effort rollback also fails.
            }
            throw;
        }

        await db.AddAuditAsync(
            "VM_GUEST_RDP_PORT_SYNCED",
            "vm",
            vmName,
            $"Windows RDP listener and WinNAT aligned to TCP {targetPort} by {currentUser!.Username}.");

        return status;
    }

    public async Task<string> ResetStoredVmPasswordAsync(PanelUser? currentUser, string vmName, string newPassword)
    {
        RequireAdmin(currentUser);
        ValidatePassword(newPassword);

        var existing = await db.GetVmAccessInfoAsync(vmName);
        var updated = existing with
        {
            Password = newPassword,
            UpdatedAt = DateTime.UtcNow
        };
        await db.SaveVmAccessInfoAsync(updated);
        await db.AddAuditAsync("VM_STORED_PASSWORD_RESET", "vm", vmName, $"By {currentUser!.Username}");

        await NotifyAssignedUsersAsync(vmName,
            $"PenguinPanel: VM password updated - {vmName}",
            $"The stored login password for {vmName} was updated in the panel. Sign in to view the new access details.");

        return "Stored VM password updated in the panel. This does not change the guest OS password unless you also change it inside the VM.";
    }

    public async Task<string> ChangeVmRemoteDesktopPasswordAsync(PanelUser? currentUser, string vmName, string newPassword)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewAccess, "Access details are not enabled for your account.");
        var requester = currentUser!;
        ValidatePassword(newPassword);

        var state = await powerShell.GetVmStateAsync(vmName);
        if (!IsRunningLikeState(state))
        {
            throw new InvalidOperationException($"The VM must be running to change the Windows password. Current state: {state}.");
        }

        var existing = await db.GetVmAccessInfoAsync(vmName);
        await powerShell.ChangeWindowsGuestPasswordAsync(vmName, existing.Username, existing.Password, newPassword);

        var updated = existing with
        {
            Password = newPassword,
            UpdatedAt = DateTime.UtcNow
        };
        await db.SaveVmAccessInfoAsync(updated);
        await db.AddAuditAsync("VM_RDP_PASSWORD_CHANGED", "vm", vmName, $"By {currentUser!.Username}");

        return "Remote Desktop password changed inside the VM and saved in the panel.";
    }


    public async Task<VmGuestAgentSettings> GetVmGuestAgentSettingsAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewAccess, "Access details are not enabled for your account.");
        return await db.GetVmGuestAgentSettingsAsync(vmName);
    }

    public async Task<IReadOnlyList<GuestAgentCommand>> GetGuestAgentCommandsForVmAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireAdmin(currentUser);
        return await db.GetGuestAgentCommandsForVmAsync(vmName);
    }

    public async Task<GuestAgentTokenResult> GenerateVmGuestAgentTokenAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireAdmin(currentUser);

        var token = "pga_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var settings = await db.SaveVmGuestAgentTokenAsync(vmName, token);
        await db.AddAuditAsync("VM_GUEST_AGENT_TOKEN_ROTATED", "vm", vmName, $"By {currentUser!.Username}");
        return new GuestAgentTokenResult(settings, token);
    }

    public async Task<VmGuestAgentSettings> SetVmGuestAgentEnabledAsync(PanelUser? currentUser, string vmName, bool enabled)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireAdmin(currentUser);

        var settings = await db.SetVmGuestAgentEnabledAsync(vmName, enabled);
        await db.AddAuditAsync(enabled ? "VM_GUEST_AGENT_ENABLED" : "VM_GUEST_AGENT_DISABLED", "vm", vmName, $"By {currentUser!.Username}");
        return settings;
    }

    public async Task<string> RequestEmergencyGuestPasswordResetCodeAsync(PanelUser? currentUser, string vmName, string username)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireEmergencyPasswordResetAccess(currentUser);
        var requester = currentUser!;

        var existingAccess = await db.GetVmAccessInfoAsync(vmName);
        if (!requester.IsAdmin)
        {
            username = string.IsNullOrWhiteSpace(existingAccess.Username) ? "Administrator" : existingAccess.Username.Trim();
        }

        if (!email.IsEnabled)
        {
            throw new InvalidOperationException("SMTP must be enabled before emergency reset verification codes can be sent.");
        }

        username = string.IsNullOrWhiteSpace(username) ? "Administrator" : username.Trim();
        var settings = await db.GetVmGuestAgentSettingsAsync(vmName);
        if (!settings.IsEnabled || !settings.HasToken)
        {
            throw new InvalidOperationException("Guest Agent is not enabled for this VM. An admin must generate and install a VM Guest Agent token first.");
        }

        var to = await db.GetUserEmailAsync(requester.Id);
        if (string.IsNullOrWhiteSpace(to))
        {
            throw new InvalidOperationException("Your panel account does not have an email address. Add an email address before using emergency password reset.");
        }

        var key = EmergencyResetCodeKey(requester.Id, vmName);
        var now = DateTime.UtcNow;
        if (_emergencyResetCodes.TryGetValue(key, out var existing)
            && existing.LastSentAt.AddSeconds(EmergencyResetCodeCooldownSeconds) > now)
        {
            var secondsLeft = Math.Max(1, (int)Math.Ceiling((existing.LastSentAt.AddSeconds(EmergencyResetCodeCooldownSeconds) - now).TotalSeconds));
            throw new InvalidOperationException($"A reset code was sent recently. Wait {secondsLeft} seconds before requesting another code.");
        }

        var code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var expiresAt = now.AddMinutes(EmergencyResetCodeMinutes);
        _emergencyResetCodes[key] = new EmergencyResetCodeState(code, expiresAt, now, 0);

        var panelUrl = PanelPublicUrl;
        var body = $"Hello {requester.Username},\n\nYour PenguinPanel emergency password reset verification code is: {code}\n\nVM: {vmName}\nWindows username: {username}\nExpires: {expiresAt:u}\n\nOnly enter this code if you requested an emergency Windows/RDP password reset.";
        var sent = await TrySendEmailAsync(to,
            $"PenguinPanel emergency reset code - {vmName}",
            body,
            BuildPanelEmailHtml(
                "Emergency verification",
                "Your reset code",
                $"Hello {requester.Username}, enter this code in PenguinPanel to approve the emergency Windows/RDP password reset.",
                [
                    new("VM", vmName),
                    new("Windows username", username),
                    new("Verification code", code, true),
                    new("Expires", expiresAt.ToLocalTime().ToString())
                ],
                "Only use this code if you requested the emergency reset. If you did not request it, change your PenguinPanel password and contact an admin.",
                "Open PenguinPanel",
                panelUrl));

        if (!sent)
        {
            _emergencyResetCodes.TryRemove(key, out _);
            throw new InvalidOperationException("The reset code could not be sent. Check SMTP settings and the account email address.");
        }

        await db.AddAuditAsync("VM_EMERGENCY_RESET_CODE_SENT", "vm", vmName, $"To {to}, requested by {requester.Username}");
        return $"Emergency reset code sent to {MaskEmail(to)}. It expires in {EmergencyResetCodeMinutes} minutes.";
    }

    public async Task<EmergencyPasswordResetQueueResult> QueueEmergencyGuestPasswordResetAsync(
        PanelUser? currentUser,
        string vmName,
        string username,
        string newPassword,
        string verificationCode,
        bool emailPasswordToCurrentUser,
        bool forceLogoffSessions,
        bool restartRemoteDesktopService,
        bool disableOtherLocalAdministrators,
        bool disableRemoteDesktopFirewall)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireEmergencyPasswordResetAccess(currentUser);
        var requester = currentUser!;
        ValidatePassword(newPassword);

        var existing = await db.GetVmAccessInfoAsync(vmName);
        if (!requester.IsAdmin)
        {
            username = string.IsNullOrWhiteSpace(existing.Username) ? "Administrator" : existing.Username.Trim();
            forceLogoffSessions = true;
            restartRemoteDesktopService = true;
            disableOtherLocalAdministrators = false;
            disableRemoteDesktopFirewall = false;
        }

        username = string.IsNullOrWhiteSpace(username) ? "Administrator" : username.Trim();
        var settings = await db.GetVmGuestAgentSettingsAsync(vmName);
        if (!settings.IsEnabled || !settings.HasToken)
        {
            throw new InvalidOperationException("Guest Agent is not enabled for this VM. An admin must generate and install a VM Guest Agent token first.");
        }

        await ValidateEmergencyResetCodeAsync(requester, vmName, verificationCode);

        var payload = new GuestAgentPasswordResetPayload(
            username,
            newPassword,
            forceLogoffSessions,
            restartRemoteDesktopService,
            disableOtherLocalAdministrators,
            disableRemoteDesktopFirewall);

        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);
        var commandId = await db.CreateGuestAgentCommandAsync(vmName, "reset-windows-password", payloadJson);

        var updated = existing with
        {
            Username = username,
            Password = newPassword,
            UpdatedAt = DateTime.UtcNow
        };
        await db.SaveVmAccessInfoAsync(updated);

        var passwordEmailSent = false;
        if (emailPasswordToCurrentUser)
        {
            passwordEmailSent = await SendEmergencyPasswordEmailAsync(requester, vmName, username, newPassword, commandId, settings.IsOnline);
        }

        await db.AddAuditAsync("VM_EMERGENCY_PASSWORD_RESET_QUEUED", "vm", vmName, $"Command #{commandId}, user {username}, by {requester.Username}, password email sent: {passwordEmailSent}");
        await NotifyAssignedUsersAsync(vmName,
            $"PenguinPanel: Emergency password reset queued - {vmName}",
            $"An emergency Windows/RDP password reset was queued for {vmName}. The saved panel password has been updated. Command id: {commandId}. The new password is only sent to the user who approved the reset if they selected email delivery.");

        var baseMessage = settings.IsOnline
            ? $"Emergency password reset command #{commandId} queued. The Guest Agent is online, so it should run shortly."
            : $"Emergency password reset command #{commandId} queued. The Guest Agent is not online right now; it will run when the VM boots and the agent checks in.";

        if (emailPasswordToCurrentUser)
        {
            baseMessage += passwordEmailSent
                ? " The new password was emailed to your panel account email."
                : " The reset was queued, but the password email failed. Copy the password from the page before leaving.";
        }

        return new EmergencyPasswordResetQueueResult(baseMessage, passwordEmailSent);
    }

    public async Task<GuestAgentHeartbeatResponse> RegisterGuestAgentHeartbeatAsync(string token, string agentVersion, string remoteIpAddress)
    {
        var settings = await db.RegisterGuestAgentHeartbeatAsync(token, agentVersion, remoteIpAddress);
        return new GuestAgentHeartbeatResponse(settings.VmName, "ok", DateTime.UtcNow);
    }

    public async Task<GuestAgentCommandResponse?> StartNextGuestAgentCommandAsync(string token)
    {
        var command = await db.StartNextGuestAgentCommandAsync(token);
        if (command is null)
        {
            return null;
        }

        await db.AddAuditAsync("VM_GUEST_AGENT_COMMAND_STARTED", "vm", command.VmName, $"Command #{command.Id}: {command.CommandType}");
        return new GuestAgentCommandResponse(command.Id, command.CommandType, command.PayloadJson);
    }

    public async Task CompleteGuestAgentCommandAsync(string token, long commandId, bool success, string message)
    {
        await db.CompleteGuestAgentCommandAsync(token, commandId, success, message);
        await db.AddAuditAsync(success ? "VM_GUEST_AGENT_COMMAND_COMPLETED" : "VM_GUEST_AGENT_COMMAND_FAILED", "guest-agent", commandId.ToString(), message);
    }

    public async Task<VmNetworkPortProfile> GetVmNetworkPortProfileAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireAdmin(currentUser);
        return await db.GetVmNetworkPortProfileAsync(vmName);
    }

    public async Task SaveVmNetworkPortProfileAsync(PanelUser? currentUser, VmNetworkPortProfile profile, bool updateAccessDetails = true)
    {
        await EnsureCanAccessVmAsync(currentUser, profile.VmName);
        RequireAdmin(currentUser);
        profile = NormalizeNetworkProfile(profile);
        var conflicts = await GetNetworkProfileConflictsAsync(profile);
        if (conflicts.Count > 0)
        {
            throw new InvalidOperationException("Port conflict: " + string.Join("; ", conflicts.Select(item => $"{item.Protocol}/{item.ExternalPort} is already used by {item.ConflictVmName}")));
        }

        await db.SaveVmNetworkPortProfileAsync(profile);

        if (updateAccessDetails)
        {
            await UpdateAccessFromNetworkProfileAsync(profile);
        }

        await db.AddAuditAsync("VM_NETWORK_PROFILE_SAVED", "vm", profile.VmName, $"By {currentUser!.Username}. RDP {profile.RdpExternalPort}->{profile.RdpInternalPort}, FiveM {(profile.EnableFiveM ? $"{profile.FiveMExternalPort}->{profile.FiveMInternalPort}" : "disabled")}");
    }

    public async Task<NetworkProfileApplyResult> ApplyVmNetworkPortProfileAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireAdmin(currentUser);
        var profile = NormalizeNetworkProfile(await db.GetVmNetworkPortProfileAsync(vmName));
        var conflicts = await GetNetworkProfileConflictsAsync(profile);
        if (conflicts.Count > 0)
        {
            throw new InvalidOperationException("Port conflict: " + string.Join("; ", conflicts.Select(item => $"{item.Protocol}/{item.ExternalPort} is already used by {item.ConflictVmName}")));
        }

        var result = await powerShell.ApplyNetworkPortProfileAsync(profile);
        await UpdateAccessFromNetworkProfileAsync(profile);
        var dnsMessage = await QueueGuestDnsUpdateIfConfiguredAsync(currentUser!, profile);
        var finalResult = result with { Message = result.Message + dnsMessage };
        await db.AddAuditAsync("VM_NETWORK_PROFILE_APPLIED", "vm", vmName, $"{finalResult.Message} By {currentUser!.Username}");
        return finalResult;
    }

    public async Task<string> ApplyVmGuestDnsSettingsAsync(PanelUser? currentUser, VmNetworkPortProfile profile)
    {
        await EnsureCanAccessVmAsync(currentUser, profile.VmName);
        RequireAdmin(currentUser);
        profile = NormalizeNetworkProfile(profile);
        var conflicts = await GetNetworkProfileConflictsAsync(profile);
        if (conflicts.Count > 0)
        {
            throw new InvalidOperationException("Port conflict: " + string.Join("; ", conflicts.Select(item => $"{item.Protocol}/{item.ExternalPort} is already used by {item.ConflictVmName}")));
        }

        await db.SaveVmNetworkPortProfileAsync(profile);
        return await QueueGuestDnsUpdateIfConfiguredAsync(currentUser!, profile, requireConfiguredDns: true);
    }

    public async Task<GuestNetworkRepairResult> RepairVmGuestInternetAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireAdmin(currentUser);

        var profile = NormalizeNetworkProfile(await db.GetVmNetworkPortProfileAsync(vmName));
        if (string.IsNullOrWhiteSpace(profile.InternalIpAddress))
        {
            throw new InvalidOperationException("Fill Internal VM IPv4 before repairing guest internet.");
        }

        var access = await db.GetVmAccessInfoAsync(vmName);
        if (string.IsNullOrWhiteSpace(access.Username) || string.IsNullOrWhiteSpace(access.Password))
        {
            throw new InvalidOperationException("Save the Windows username and password on the Access tab first. Guest internet repair uses PowerShell Direct, so it needs local Windows credentials but does not need the VM to have internet yet.");
        }

        var result = await powerShell.RepairWindowsGuestNetworkAsync(profile, access.Username, access.Password, PanelPublicUrl);
        await db.AddAuditAsync("VM_GUEST_INTERNET_REPAIRED", "vm", vmName, $"By {currentUser!.Username}. {result.Message}");
        return result;
    }

    public async Task<IReadOnlyList<VmGuestFirewallRule>> GetVmGuestFirewallRulesAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewNetwork, "Windows Firewall management is not enabled for your account.");
        var access = await RequireGuestFirewallCredentialsAsync(vmName);
        return await powerShell.GetWindowsGuestFirewallRulesAsync(vmName, access.Username, access.Password);
    }

    public async Task<VmGuestFirewallRule> AddVmGuestFirewallRuleAsync(PanelUser? currentUser, string vmName, string displayName, string protocol, int localPort, string remoteAddress, string action)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewNetwork, "Windows Firewall management is not enabled for your account.");
        if (!currentUser.IsAdmin && !string.Equals(action, "Allow", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Client firewall rules can only allow inbound traffic. Contact support if you need a custom block rule.");
        }

        var access = await RequireGuestFirewallCredentialsAsync(vmName);
        var result = await powerShell.AddWindowsGuestFirewallRuleAsync(vmName, access.Username, access.Password, displayName, protocol, localPort, remoteAddress, action);
        await db.AddAuditAsync("VM_GUEST_FIREWALL_RULE_ADDED", "vm", vmName, $"{result.DisplayName}: {result.Action} {result.Protocol}/{result.LocalPort}, remote {result.RemoteAddress}, by {currentUser.Username}");
        return result;
    }

    public async Task RemoveVmGuestFirewallRuleAsync(PanelUser? currentUser, string vmName, string ruleName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewNetwork, "Windows Firewall management is not enabled for your account.");
        var access = await RequireGuestFirewallCredentialsAsync(vmName);
        await powerShell.RemoveWindowsGuestFirewallRuleAsync(vmName, access.Username, access.Password, ruleName);
        await db.AddAuditAsync("VM_GUEST_FIREWALL_RULE_REMOVED", "vm", vmName, $"Rule {ruleName}, by {currentUser.Username}");
    }

    public async Task<GuestFirewallApplyResult> SyncVmGuestFirewallFromNetworkProfileAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewNetwork, "Windows Firewall management is not enabled for your account.");
        var access = await RequireGuestFirewallCredentialsAsync(vmName);
        var profile = NormalizeNetworkProfile(await db.GetVmNetworkPortProfileAsync(vmName));
        var result = await powerShell.SyncWindowsGuestFirewallRulesAsync(profile, access.Username, access.Password);
        await db.AddAuditAsync("VM_GUEST_FIREWALL_SYNCED", "vm", vmName, $"{result.Message} By {currentUser.Username}");
        return result;
    }

    public async Task<IReadOnlyList<PortMappingConflict>> CheckVmNetworkPortConflictsAsync(PanelUser? currentUser, VmNetworkPortProfile profile)
    {
        await EnsureCanAccessVmAsync(currentUser, profile.VmName);
        RequireAdmin(currentUser);
        return await GetNetworkProfileConflictsAsync(NormalizeNetworkProfile(profile));
    }

    public async Task<Dictionary<long, UserResourceLimits>> GetUserResourceLimitsAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await db.GetUserResourceLimitsAsync();
    }

    public async Task SaveUserResourceLimitsAsync(PanelUser? currentUser, UserResourceLimits limits)
    {
        RequireAdmin(currentUser);
        if (limits.UserId <= 0)
        {
            throw new InvalidOperationException("Select a user before saving limits.");
        }

        await db.SaveUserResourceLimitsAsync(limits with
        {
            MaxVmCount = Math.Max(0, limits.MaxVmCount),
            MaxCpuCores = Math.Max(0, limits.MaxCpuCores),
            MaxMemoryMb = Math.Max(0, limits.MaxMemoryMb),
            MaxDiskGb = Math.Max(0, limits.MaxDiskGb),
            MaxPowerActionsPerHour = Math.Max(0, limits.MaxPowerActionsPerHour)
        });
        await db.AddAuditAsync("USER_RESOURCE_LIMITS_UPDATED", "user", limits.UserId.ToString(), $"By {currentUser!.Username}");
    }

    public async Task<VmPublicStatusSettings> GetVmPublicStatusSettingsAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireAdmin(currentUser);
        return await db.GetVmPublicStatusSettingsAsync(vmName);
    }

    public async Task SaveVmPublicStatusSettingsAsync(PanelUser? currentUser, VmPublicStatusSettings settings)
    {
        await EnsureCanAccessVmAsync(currentUser, settings.VmName);
        RequireAdmin(currentUser);
        var normalized = settings with
        {
            DisplayName = string.IsNullOrWhiteSpace(settings.DisplayName) ? settings.VmName : settings.DisplayName.Trim(),
            PublicNote = settings.PublicNote?.Trim() ?? string.Empty,
            PortCheckPort = settings.PortCheckPort is >= 1 and <= 65535 ? settings.PortCheckPort : null,
            UpdatedAt = DateTime.UtcNow
        };

        await db.SaveVmPublicStatusSettingsAsync(normalized);
        await db.AddAuditAsync("VM_PUBLIC_STATUS_UPDATED", "vm", settings.VmName, $"Public: {normalized.IsPublic}, by {currentUser!.Username}");
    }

    public async Task<IReadOnlyList<VmPublicStatus>> GetPublicVmStatusesAsync()
    {
        var settings = await db.GetPublicVmStatusSettingsAsync(publicOnly: true);
        if (settings.Count == 0)
        {
            return [];
        }

        var allVms = await ReadAllVmsAsync();
        var result = new List<VmPublicStatus>();
        foreach (var item in settings)
        {
            var vm = allVms.FirstOrDefault(candidate => string.Equals(candidate.Name, item.VmName, StringComparison.OrdinalIgnoreCase));
            var state = vm?.State ?? "Unknown";
            var access = await db.GetVmAccessInfoAsync(item.VmName);
            var address = string.IsNullOrWhiteSpace(access.Hostname) ? access.IpAddress : access.Hostname;
            result.Add(new VmPublicStatus(
                item.VmName,
                string.IsNullOrWhiteSpace(item.DisplayName) ? item.VmName : item.DisplayName,
                state,
                IsRunningLikeState(state),
                vm?.CpuUsage ?? 0,
                vm?.MemoryAssigned ?? 0,
                vm?.Uptime ?? TimeSpan.Zero,
                address ?? string.Empty,
                item.PortCheckPort ?? access.RdpPort,
                item.PublicNote ?? string.Empty,
                DateTime.UtcNow));
        }

        return result;
    }

    public async Task<VmDeleteResult> DeleteVmAsync(PanelUser? currentUser, string vmName)
    {
        RequireAdmin(currentUser);
        vmName = (vmName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(vmName))
        {
            throw new InvalidOperationException("Select a VPS to delete.");
        }

        await _provisioningLock.WaitAsync();
        try
        {
            var existing = (await powerShell.GetVmsAsync())
                .FirstOrDefault(item => string.Equals(item.Name, vmName, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                throw new InvalidOperationException($"Virtual machine '{vmName}' was not found on this Hyper-V host.");
            }

            var profile = await db.GetVmNetworkPortProfileAsync(vmName);
            var defaults = ReadVmProvisioningDefaults();
            await db.AddAuditAsync("VM_DELETE_STARTED", "vm", vmName, $"Permanent VPS deletion started by {currentUser!.Username}.");

            NetworkProfileCleanupResult networkCleanup;
            try
            {
                networkCleanup = await powerShell.RemoveNetworkPortProfileAsync(profile);
                await db.AddAuditAsync("VM_DELETE_NETWORK_CLEANED", "vm", vmName, networkCleanup.Message);
            }
            catch (Exception ex)
            {
                await db.AddAuditAsync("VM_DELETE_BLOCKED_NETWORK_CLEANUP", "vm", vmName, ex.Message);
                throw new InvalidOperationException("The VPS was NOT deleted because its host WinNAT/firewall cleanup failed. Fix the cleanup error first so no stale ports are left behind. " + ex.Message, ex);
            }

            VmDeleteResult deleted;
            try
            {
                deleted = await powerShell.DeleteVmAndStorageAsync(vmName, defaults.VmStorageRoots);
            }
            catch (Exception ex)
            {
                string recoveryMessage;
                try
                {
                    var reapplied = await powerShell.ApplyNetworkPortProfileAsync(profile);
                    recoveryMessage = " The previous NAT/firewall profile was restored because VM deletion failed. " + reapplied.Message;
                    await db.AddAuditAsync("VM_DELETE_NETWORK_RESTORED", "vm", vmName, reapplied.Message);
                }
                catch (Exception restoreEx)
                {
                    recoveryMessage = " WARNING: VM deletion failed and PenguinPanel could not restore the previous NAT/firewall profile automatically: " + restoreEx.Message;
                    await db.AddAuditAsync("VM_DELETE_NETWORK_RESTORE_FAILED", "vm", vmName, restoreEx.Message);
                }

                await db.AddAuditAsync("VM_DELETE_FAILED", "vm", vmName, ex.Message + recoveryMessage);
                throw new InvalidOperationException("The VM itself could not be deleted." + recoveryMessage + " Original error: " + ex.Message, ex);
            }

            await db.DeleteVmDataAsync(vmName);
            var result = deleted with
            {
                NatMappingsRemoved = networkCleanup.NatMappingsRemoved,
                HostFirewallRulesRemoved = networkCleanup.HostFirewallRulesRemoved,
                Message = $"Deleted {vmName}. Removed {networkCleanup.NatMappingsRemoved} WinNAT mapping(s), {networkCleanup.HostFirewallRulesRemoved} root Windows Firewall rule(s), the Hyper-V VM, and its managed storage folder."
            };

            await db.AddAuditAsync("VM_DELETED", "vm", vmName, $"{result.Message} By {currentUser.Username}.");
            return result;
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    public VmProvisioningDefaults GetVmProvisioningDefaults(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return ReadVmProvisioningDefaults();
    }

    public IReadOnlyList<VmStorageRootStatus> GetVmStorageRootStatuses(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        var defaults = ReadVmProvisioningDefaults();
        return ReadVmStorageRootStatuses(defaults);
    }

    public int GetMinimumStorageReserveGb(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return ReadMinimumFreeSpaceAfterProvisionGb();
    }

    public int GetMinimumStorageReservePercent(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return ReadMinimumFreeSpaceAfterProvisionPercent();
    }

    public string GetStorageSelectionMode(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        var mode = (configuration["Provisioning:StorageSelectionMode"] ?? "MostFreeSpace").Trim();
        if (string.Equals(mode, "DefaultFirst", StringComparison.OrdinalIgnoreCase)) return "DefaultFirst";
        if (string.Equals(mode, "MostFreePercent", StringComparison.OrdinalIgnoreCase)) return "MostFreePercent";
        return "MostFreeSpace";
    }

    public async Task<VmResourceResizeResult> ResizeVmResourcesAsync(PanelUser? currentUser, string vmName, int cpuCores, int memoryMb, int diskGb)
    {
        RequireAdmin(currentUser);

        vmName = (vmName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(vmName))
        {
            throw new InvalidOperationException("VM name is required.");
        }

        var cpu = Math.Clamp(cpuCores, 1, 64);
        var memory = Math.Clamp(memoryMb, 512, 1048576);
        var disk = Math.Clamp(diskGb, 1, 16384);
        var current = await powerShell.GetVmResourceConfigurationAsync(vmName);
        var oldDiskGb = (int)Math.Ceiling(current.DiskSizeBytes / 1024d / 1024d / 1024d);

        if (disk < oldDiskGb)
        {
            throw new InvalidOperationException(
                $"Disk downgrade is not supported because shrinking a VHD/VHDX safely requires shrinking the guest partition first. " +
                $"Current virtual disk: {oldDiskGb} GB; requested: {disk} GB.");
        }

        if (disk > oldDiskGb)
        {
            var defaults = ReadVmProvisioningDefaults();
            var storageStatuses = ReadVmStorageRootStatuses(defaults);
            var storageStatus = storageStatuses.FirstOrDefault(item => IsPathInsideStorageRoot(current.DiskPath, item.Path));
            if (storageStatus is null)
            {
                throw new InvalidOperationException(
                    $"The VM disk '{current.DiskPath}' is not inside an approved PenguinPanel VM storage root, so PenguinPanel cannot safely validate free space before growing it.");
            }
            if (!storageStatus.IsAvailable)
            {
                throw new InvalidOperationException($"VM storage root '{storageStatus.Path}' is unavailable. {storageStatus.Error}");
            }

            var reserveGb = ReadMinimumFreeSpaceAfterProvisionGb();
            var reservePercent = ReadMinimumFreeSpaceAfterProvisionPercent();
            var reserveBytes = CalculateStorageReserveBytes(storageStatus, reserveGb, reservePercent);
            var targetBytes = checked((long)disk * 1024L * 1024L * 1024L);
            var additionalBytes = Math.Max(0, targetBytes - current.DiskSizeBytes);
            if (storageStatus.FreeBytes < additionalBytes + reserveBytes)
            {
                throw new InvalidOperationException(
                    $"VM storage root '{storageStatus.Path}' does not have enough free space to grow this VPS from {oldDiskGb} GB to {disk} GB while keeping the configured safety reserve. " +
                    $"Free now: {FormatBytesAsGb(storageStatus.FreeBytes)} GB; extra virtual disk requested: {FormatBytesAsGb(additionalBytes)} GB; reserve: {FormatBytesAsGb(reserveBytes)} GB.");
            }
        }

        var hyperV = await powerShell.ResizeVmResourcesAsync(vmName, cpu, memory, disk);
        var warnings = new List<string>();
        var guestPartitionExpanded = false;

        if (hyperV.NewDiskSizeBytes > hyperV.OldDiskSizeBytes)
        {
            var access = await db.GetVmAccessInfoAsync(vmName);
            if (string.IsNullOrWhiteSpace(access.Username) || string.IsNullOrWhiteSpace(access.Password))
            {
                warnings.Add("The VHD/VHDX was expanded, but PenguinPanel has no saved Windows credentials to expand the C: partition automatically.");
            }
            else if (!string.Equals(hyperV.FinalState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("The VHD/VHDX was expanded while the VPS was stopped. Start Windows and expand the C: partition before expecting the additional space inside the guest.");
            }
            else
            {
                try
                {
                    await powerShell.WaitForPowerShellDirectAsync(vmName, access.Username, access.Password, 240);
                    guestPartitionExpanded = await powerShell.ExpandWindowsGuestSystemPartitionAsync(vmName, access.Username, access.Password);
                    if (!guestPartitionExpanded)
                    {
                        warnings.Add("The VHD/VHDX was expanded, but Windows reported that C: was already at its maximum supported partition size.");
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("The VHD/VHDX was expanded, but Windows C: could not be expanded automatically: " + ex.Message);
                }
            }
        }

        var newDiskGb = (int)Math.Ceiling(hyperV.NewDiskSizeBytes / 1024d / 1024d / 1024d);
        await db.AddAuditAsync(
            "VM_RESOURCES_CHANGED",
            "vm",
            vmName,
            $"CPU {hyperV.OldCpuCores}->{hyperV.NewCpuCores}, RAM {hyperV.OldMemoryMb}->{hyperV.NewMemoryMb} MB, disk {oldDiskGb}->{newDiskGb} GB, restarted {hyperV.Restarted}, warnings {warnings.Count}, by {currentUser!.Username}");

        return new VmResourceResizeResult(
            vmName,
            hyperV.PreviousState,
            hyperV.FinalState,
            hyperV.OldCpuCores,
            hyperV.NewCpuCores,
            hyperV.OldMemoryMb,
            hyperV.NewMemoryMb,
            oldDiskGb,
            newDiskGb,
            hyperV.DiskPath,
            hyperV.Restarted,
            guestPartitionExpanded,
            warnings,
            warnings.Count == 0
                ? "VPS resources updated successfully."
                : "VPS resources were updated with warning(s): " + string.Join(" | ", warnings));
    }

    public async Task<VmProvisionResult> ProvisionVmAsync(PanelUser? currentUser, VmProvisionRequest request)
    {
        RequireAdmin(currentUser);
        if (request.UserId <= 0)
        {
            throw new InvalidOperationException("Select the customer who should own this VPS.");
        }

        var target = await db.GetUserByIdAsync(request.UserId)
            ?? throw new InvalidOperationException("The selected customer was not found.");

        if (target.IsSubUser)
        {
            throw new InvalidOperationException("Provision VPSs to the main customer account, not to a sub-user.");
        }

        var vmName = NormalizeProvisioningVmName(request.VmName);
        var template = (await db.GetVmTemplatesAsync(enabledOnly: false)).FirstOrDefault(item => item.Id == request.TemplateId)
            ?? throw new InvalidOperationException("Select a valid VHD/VHDX template.");
        var extension = Path.GetExtension(template.IsoPath);
        if (!string.Equals(extension, ".vhd", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".vhdx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Automatic VPS creation requires a VHD or VHDX template.");
        }

        var cpu = Math.Clamp(request.CpuCores, 1, 64);
        var memoryMb = Math.Clamp(request.MemoryMb, 512, 1048576);
        var diskGb = Math.Clamp(request.DiskGb, 1, 16384);
        var generation = request.Generation == 1 ? 1 : 2;
        var defaults = ReadVmProvisioningDefaults();
        var storageRoot = ResolveProvisioningStorageRoot(defaults, request.StorageRoot, diskGb);
        var fiveMInternalPort = request.FiveMInternalPort is >= 1 and <= 65535
            ? request.FiveMInternalPort
            : defaults.DefaultFiveMInternalPort;

        await EnforceVmAssignmentLimitAsync(target, vmName);
        await EnforceProvisioningResourceLimitsAsync(target, cpu, memoryMb, diskGb);

        if (request.ConfigureWindowsGuest && string.IsNullOrWhiteSpace(request.TemplatePassword))
        {
            throw new InvalidOperationException("Template/current Windows password is required when automatic guest IP/password configuration is enabled.");
        }

        await _provisioningLock.WaitAsync();
        try
        {
            var allVms = await powerShell.GetVmsAsync();
            if (allVms.Any(item => string.Equals(item.Name, vmName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A virtual machine named '{vmName}' already exists.");
            }

            var internalIp = await AllocateProvisioningIpAsync(defaults, request.InternalIpAddress);
            var rdpPort = await AllocateProvisioningRdpPortAsync(defaults, request.RdpExternalPort);
            int? fiveMPort = request.EnableFiveM
                ? await AllocateProvisioningFiveMPortAsync(defaults, request.FiveMExternalPort, rdpPort)
                : null;
            if (request.ConfigureWindowsGuest && request.EnableFiveM && fiveMInternalPort == rdpPort)
            {
                throw new InvalidOperationException($"FiveM VM TCP port {fiveMInternalPort} cannot be the same as the Windows RDP port {rdpPort}. Choose a different FiveM VM port or RDP port.");
            }
            var warnings = new List<string>();
            var guestUsername = string.IsNullOrWhiteSpace(request.GuestUsername) ? "Administrator" : request.GuestUsername.Trim();
            var newGuestPassword = request.ConfigureWindowsGuest
                ? (string.IsNullOrWhiteSpace(request.NewGuestPassword) ? GenerateFriendlyPassword() : request.NewGuestPassword)
                : string.Empty;

            await db.AddAuditAsync("VM_PROVISION_STARTED", "vm", vmName,
                $"Template {template.Name}, owner {target.Username}, storage {storageRoot}, CPU {cpu}, RAM {memoryMb} MB, disk {diskGb} GB, IP {internalIp}, RDP {rdpPort}, FiveM {(request.EnableFiveM ? $"{fiveMPort}->{fiveMInternalPort} TCP/UDP" : "disabled")}, by {currentUser!.Username}");

            VmHyperVProvisionResult hyperVResult;
            try
            {
                hyperVResult = await powerShell.ProvisionVmFromTemplateAsync(
                    vmName,
                    template.IsoPath,
                    storageRoot,
                    SafeProvisioningFolderName(target.Username),
                    defaults.SwitchName,
                    cpu,
                    memoryMb,
                    diskGb,
                    generation,
                    request.SecureBoot,
                    request.StartAfterProvision || request.ConfigureWindowsGuest);
            }
            catch (Exception ex)
            {
                await db.AddAuditAsync("VM_PROVISION_FAILED", "vm", vmName, ex.Message);
                throw;
            }

            var profile = new VmNetworkPortProfile(
                vmName,
                defaults.NatName,
                internalIp,
                defaults.PublicAddress,
                defaults.PublicAddress,
                true,
                rdpPort,
                3389,
                request.EnableFiveM,
                fiveMPort,
                fiveMInternalPort,
                string.Empty,
                string.Empty,
                string.Empty,
                defaults.PreferredDnsServer,
                defaults.AlternateDnsServer,
                DateTime.UtcNow);

            try
            {
                var conflicts = await GetNetworkProfileConflictsAsync(profile);
                if (conflicts.Count > 0)
                {
                    throw new InvalidOperationException("Generated network port conflicts with an existing profile: " + string.Join("; ", conflicts.Select(item => $"{item.Protocol}/{item.ExternalPort} used by {item.ConflictVmName}")));
                }

                // Reserve the profile now, but delay WinNAT until guest setup establishes the
                // real Windows RDP listener port. This prevents a public mapping from pointing
                // at a port Windows is not listening on yet.
                await db.SaveVmNetworkPortProfileAsync(profile);
            }
            catch (Exception ex)
            {
                warnings.Add("VM was created, but the generated network profile could not be reserved: " + ex.Message);
                await db.AddAuditAsync("VM_PROVISION_NETWORK_WARNING", "vm", vmName, ex.Message);
            }

            var passwordForClient = string.Empty;
            if (request.ConfigureWindowsGuest)
            {
                try
                {
                    await powerShell.WaitForPowerShellDirectAsync(vmName, guestUsername, request.TemplatePassword, 240);
                    var repair = await powerShell.RepairWindowsGuestNetworkAsync(profile, guestUsername, request.TemplatePassword, PanelPublicUrl);
                    await db.AddAuditAsync("VM_GUEST_NETWORK_CONFIGURED", "vm", vmName, repair.Message);

                    try
                    {
                        var rdpStatus = await powerShell.SetWindowsGuestRdpPortAsync(vmName, guestUsername, request.TemplatePassword, rdpPort);
                        profile = profile with { RdpInternalPort = rdpPort, UpdatedAt = DateTime.UtcNow };
                        await db.SaveVmNetworkPortProfileAsync(profile);
                        await db.AddAuditAsync("VM_GUEST_RDP_PORT_SYNCED", "vm", vmName, $"Windows RDP listener changed to TCP {rdpStatus.Port} during provisioning.");
                    }
                    catch (Exception rdpEx)
                    {
                        warnings.Add($"VPS was created, but Windows RDP stayed on internal TCP {profile.RdpInternalPort}: " + rdpEx.Message);
                        await db.AddAuditAsync("VM_PROVISION_GUEST_RDP_WARNING", "vm", vmName, rdpEx.Message);
                    }

                    try
                    {
                        var guestFirewall = await powerShell.SyncWindowsGuestFirewallRulesAsync(profile, guestUsername, request.TemplatePassword);
                        await db.AddAuditAsync("VM_GUEST_FIREWALL_SYNCED", "vm", vmName, guestFirewall.Message);
                    }
                    catch (Exception firewallEx)
                    {
                        warnings.Add("VPS was created, but Windows Firewall rules inside the guest could not be synced automatically: " + firewallEx.Message);
                        await db.AddAuditAsync("VM_PROVISION_GUEST_FIREWALL_WARNING", "vm", vmName, firewallEx.Message);
                    }

                    if (!string.Equals(request.TemplatePassword, newGuestPassword, StringComparison.Ordinal))
                    {
                        await powerShell.ChangeWindowsGuestPasswordAsync(vmName, guestUsername, request.TemplatePassword, newGuestPassword);
                    }
                    passwordForClient = newGuestPassword;
                    await db.AddAuditAsync("VM_GUEST_PASSWORD_CONFIGURED", "vm", vmName, $"Windows password configured for {guestUsername} by {currentUser!.Username}");
                }
                catch (Exception ex)
                {
                    warnings.Add("VM was created, but Windows guest IP/password automation did not finish: " + ex.Message);
                    await db.AddAuditAsync("VM_PROVISION_GUEST_WARNING", "vm", vmName, ex.Message);
                }
            }
            else
            {
                warnings.Add("Windows guest IP/password automation was skipped. The VM NIC is connected, but configure the static guest IP manually if the template has no DHCP settings.");
            }

            try
            {
                var conflicts = await GetNetworkProfileConflictsAsync(profile);
                if (conflicts.Count > 0)
                {
                    throw new InvalidOperationException("Generated network port conflicts with an existing profile: " + string.Join("; ", conflicts.Select(item => $"{item.Protocol}/{item.ExternalPort} used by {item.ConflictVmName}")));
                }

                await db.SaveVmNetworkPortProfileAsync(profile);
                var networkResult = await powerShell.ApplyNetworkPortProfileAsync(profile);
                await db.AddAuditAsync("VM_NETWORK_PROFILE_APPLIED", "vm", vmName, networkResult.Message);
            }
            catch (Exception ex)
            {
                warnings.Add("VM was created, but automatic NAT/RDP/FiveM mapping failed: " + ex.Message);
                await db.AddAuditAsync("VM_PROVISION_NETWORK_WARNING", "vm", vmName, ex.Message);
            }

            var access = new VmAccessInfo(
                vmName,
                defaults.PublicAddress,
                defaults.PublicAddress,
                rdpPort,
                null,
                guestUsername,
                passwordForClient,
                string.IsNullOrWhiteSpace(template.Notes) ? $"Provisioned from {template.Name}." : template.Notes,
                string.Empty,
                DateTime.UtcNow);
            await db.SaveVmAccessInfoAsync(access);

            await db.AssignVmToUserAsync(target.Id, vmName);
            await db.AddAuditAsync("VM_PROVISION_COMPLETED", "vm", vmName,
                $"Assigned to {target.Username}. Storage {storageRoot}. Internal IP {internalIp}, public RDP {defaults.PublicAddress}:{rdpPort} -> {internalIp}:{profile.RdpInternalPort}, FiveM {(request.EnableFiveM ? $"{defaults.PublicAddress}:{fiveMPort} -> {internalIp}:{fiveMInternalPort} TCP/UDP" : "disabled")}. Warnings: {warnings.Count}");

            var state = await powerShell.GetVmStateAsync(vmName);
            var publicServiceHost = GetPublicServiceHost(defaults.PublicAddress);
            var rdpEndpoint = $"{publicServiceHost}:{rdpPort}";
            var fiveMEndpoint = request.EnableFiveM && fiveMPort is > 0
                ? $"{publicServiceHost}:{fiveMPort}"
                : null;

            var emailBody = $"Hello {target.Username},\n\nYour virtual server is ready.\n\nVM: {vmName}\nTemplate: {template.Name}\nRDP: {rdpEndpoint}\nWindows RDP listener: TCP {profile.RdpInternalPort}\nWindows username: {guestUsername}";
            if (!string.IsNullOrWhiteSpace(fiveMEndpoint))
            {
                emailBody += $"\nFiveM public endpoint: {fiveMEndpoint} (TCP/UDP)\nFiveM guest port: {fiveMInternalPort}";
            }
            if (!string.IsNullOrWhiteSpace(passwordForClient))
            {
                emailBody += $"\nWindows password: {passwordForClient}";
            }

            var provisioningNote = warnings.Count > 0
                ? "The VPS was created, but one or more automatic setup steps need administrator review. Please contact support if RDP is not ready yet."
                : "Your VPS is ready. Keep your Windows password private and change it if you believe it has been exposed.";

            if (warnings.Count > 0)
            {
                emailBody += $"\n\nProvisioning note: {provisioningNote}";
            }
            emailBody += $"\n\nPanel: {PanelPublicUrl}";

            var emailRows = new List<EmailRow>
            {
                new("VM", vmName),
                new("Template", template.Name),
                new("RDP", rdpEndpoint),
                new("Windows RDP listener", $"TCP {profile.RdpInternalPort}"),
                new("Windows username", guestUsername)
            };
            if (!string.IsNullOrWhiteSpace(fiveMEndpoint))
            {
                emailRows.Add(new("FiveM public endpoint", $"{fiveMEndpoint} (TCP/UDP)"));
                emailRows.Add(new("FiveM guest port", fiveMInternalPort.ToString()));
            }
            if (!string.IsNullOrWhiteSpace(passwordForClient))
            {
                emailRows.Add(new("Windows password", passwordForClient, true));
            }

            await TrySendEmailAsync(
                await db.GetUserEmailAsync(target.Id),
                $"PenguinPanel: VPS ready - {vmName}",
                emailBody,
                BuildPanelEmailHtml(
                    "VPS ready",
                    $"Your virtual server {vmName} is ready",
                    $"Hello {target.Username}, your virtual server has been provisioned and is ready to use.",
                    emailRows,
                    provisioningNote,
                    "Open PenguinPanel",
                    PanelPublicUrl));

            return new VmProvisionResult(
                vmName,
                target.Id,
                target.Username,
                template.Name,
                internalIp,
                defaults.PublicAddress,
                rdpPort,
                request.EnableFiveM ? fiveMPort : null,
                request.EnableFiveM ? fiveMInternalPort : null,
                storageRoot,
                guestUsername,
                passwordForClient,
                hyperVResult.VmPath,
                hyperVResult.DiskPath,
                state,
                warnings,
                warnings.Count == 0
                    ? $"VPS {vmName} created successfully and assigned to {target.Username}."
                    : $"VPS {vmName} was created and assigned to {target.Username}, with {warnings.Count} warning(s)." );
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    public async Task<IReadOnlyList<VmTemplate>> GetVmTemplatesAsync(PanelUser? currentUser, bool enabledOnly = false)
    {
        if (currentUser is null)
        {
            return [];
        }

        if (!currentUser.IsAdmin && !currentUser.EffectivePermissions.CanRequestReinstall)
        {
            return [];
        }

        return await db.GetVmTemplatesAsync(enabledOnly || !currentUser.IsAdmin);
    }

    public async Task<long> SaveVmTemplateAsync(PanelUser? currentUser, VmTemplate template)
    {
        RequireAdmin(currentUser);
        if (string.IsNullOrWhiteSpace(template.Name))
        {
            throw new InvalidOperationException("Template name is required.");
        }

        var id = await db.SaveVmTemplateAsync(template with { Name = template.Name.Trim() });
        await db.AddAuditAsync("VM_TEMPLATE_SAVED", "template", id.ToString(), $"{template.Name} by {currentUser!.Username}");
        return id;
    }

    public async Task<VmTemplateCreateResult> CreateTemplateFromVmAsync(PanelUser? currentUser, string sourceVmName, string templateName, string operatingSystem, string destinationFolder, string notes, bool enabled)
    {
        RequireAdmin(currentUser);
        if (string.IsNullOrWhiteSpace(sourceVmName))
        {
            throw new InvalidOperationException("Source VM is required.");
        }

        templateName = string.IsNullOrWhiteSpace(templateName)
            ? $"{sourceVmName} template"
            : templateName.Trim();
        operatingSystem = string.IsNullOrWhiteSpace(operatingSystem) ? "Windows" : operatingSystem.Trim();
        destinationFolder = string.IsNullOrWhiteSpace(destinationFolder)
            ? @"D:\PenguinPanel\Templates"
            : destinationFolder.Trim();

        var result = await powerShell.CreateTemplateFromVmDiskAsync(sourceVmName, destinationFolder, templateName);
        var template = new VmTemplate(0, templateName, operatingSystem, result.TemplatePath, notes ?? string.Empty, enabled, DateTime.UtcNow);
        var id = await db.SaveVmTemplateAsync(template);
        await db.AddAuditAsync("VM_TEMPLATE_CREATED_FROM_VM", "template", id.ToString(), $"{templateName} from {sourceVmName} by {currentUser!.Username}. Path: {result.TemplatePath}");
        return result;
    }

    public async Task<VmReinstallResult> RunReinstallRequestAsync(PanelUser? currentUser, long requestId, bool startAfter = true)
    {
        RequireAdmin(currentUser);
        var job = await db.GetReinstallJobAsync(requestId)
            ?? throw new InvalidOperationException("Reinstall request was not found.");

        var extension = Path.GetExtension(job.TemplatePath);
        if (!string.Equals(extension, ".vhd", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".vhdx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Automatic reinstall only supports VHD/VHDX templates. ISO templates can still be used for manual reinstall requests.");
        }

        await db.UpdateReinstallRequestStatusAsync(requestId, "approved");
        await db.AddAuditAsync("VM_REINSTALL_STARTED", "vm", job.VmName, $"Request #{requestId}, template {job.TemplateName}, by {currentUser!.Username}");

        try
        {
            var profile = NormalizeNetworkProfile(await db.GetVmNetworkPortProfileAsync(job.VmName));
            var result = await powerShell.ReinstallVmFromTemplateDiskAsync(job.VmName, job.TemplateName, job.TemplatePath, startAfter);
            var profileMessage = string.Empty;
            try
            {
                if (profile.HasRdpRule || profile.HasFiveMRule || profile.HasGuestDnsSettings || !string.IsNullOrWhiteSpace(profile.ExtraTcpPorts) || !string.IsNullOrWhiteSpace(profile.ExtraUdpPorts))
                {
                    var applyResult = await powerShell.ApplyNetworkPortProfileAsync(profile);
                    await UpdateAccessFromNetworkProfileAsync(profile);
                    var dnsMessage = await QueueGuestDnsUpdateIfConfiguredAsync(currentUser!, profile);
                    profileMessage = " " + applyResult.Message + dnsMessage;
                    await db.AddAuditAsync("VM_NETWORK_PROFILE_APPLIED_AFTER_REINSTALL", "vm", job.VmName, applyResult.Message + dnsMessage);
                }
            }
            catch (Exception profileEx)
            {
                profileMessage = " Reinstall completed, but port profile apply failed: " + profileEx.Message;
                await db.AddAuditAsync("VM_NETWORK_PROFILE_APPLY_FAILED", "vm", job.VmName, profileEx.Message);
            }

            await db.UpdateReinstallRequestStatusAsync(requestId, "completed");
            await db.AddAuditAsync("VM_REINSTALL_COMPLETED", "vm", job.VmName, $"Request #{requestId}, template {job.TemplateName}, new disk {result.NewDiskPath}.{profileMessage}");

            await NotifyAssignedUsersAsync(job.VmName,
                $"PenguinPanel: {job.VmName} reinstall completed",
                $"The virtual server {job.VmName} was reinstalled from template {job.TemplateName}. Current state: {result.FinalState}.{profileMessage}");

            return result with { Message = result.Message + profileMessage };
        }
        catch (Exception ex)
        {
            await db.UpdateReinstallRequestStatusAsync(requestId, "failed");
            await db.AddAuditAsync("VM_REINSTALL_FAILED", "vm", job.VmName, $"Request #{requestId}, template {job.TemplateName}, error: {ex.Message}");
            throw;
        }
    }

    public async Task<VmReinstallResult> RunDirectReinstallAsync(PanelUser? currentUser, string vmName, long templateId, bool startAfter = true, string notes = "")
    {
        RequireUserPermission(currentUser, currentUser?.EffectivePermissions.CanRequestReinstall == true, "Self-service reinstall is not enabled for your account.");
        await EnsureCanAccessVmAsync(currentUser, vmName);

        var enabledOnly = currentUser is not null && !currentUser.IsAdmin;
        var template = (await db.GetVmTemplatesAsync(enabledOnly: enabledOnly)).FirstOrDefault(item => item.Id == templateId)
            ?? throw new InvalidOperationException(enabledOnly ? "Template was not found or is not enabled for clients." : "Template was not found.");

        var extension = Path.GetExtension(template.IsoPath);
        if (!string.Equals(extension, ".vhd", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".vhdx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Automatic reinstall only supports VHD/VHDX templates. ISO templates can still be saved for manual use, but they cannot be applied automatically.");
        }

        await db.AddAuditAsync("VM_REINSTALL_STARTED", "vm", vmName, $"Direct reinstall, template {template.Name}, by {currentUser!.Username}. Notes: {notes}");

        try
        {
            var profile = NormalizeNetworkProfile(await db.GetVmNetworkPortProfileAsync(vmName));
            var result = await powerShell.ReinstallVmFromTemplateDiskAsync(vmName, template.Name, template.IsoPath, startAfter);
            var profileMessage = string.Empty;

            try
            {
                if (profile.HasRdpRule || profile.HasFiveMRule || profile.HasGuestDnsSettings || !string.IsNullOrWhiteSpace(profile.ExtraTcpPorts) || !string.IsNullOrWhiteSpace(profile.ExtraUdpPorts))
                {
                    var applyResult = await powerShell.ApplyNetworkPortProfileAsync(profile);
                    await UpdateAccessFromNetworkProfileAsync(profile);
                    var dnsMessage = await QueueGuestDnsUpdateIfConfiguredAsync(currentUser!, profile);
                    profileMessage = " " + applyResult.Message + dnsMessage;
                    await db.AddAuditAsync("VM_NETWORK_PROFILE_APPLIED_AFTER_REINSTALL", "vm", vmName, applyResult.Message + dnsMessage);
                }
            }
            catch (Exception profileEx)
            {
                profileMessage = " Reinstall completed, but port profile apply failed: " + profileEx.Message;
                await db.AddAuditAsync("VM_NETWORK_PROFILE_APPLY_FAILED", "vm", vmName, profileEx.Message);
            }

            var templateNotesMessage = string.IsNullOrWhiteSpace(template.Notes)
                ? string.Empty
                : $"\n\nAfter reinstall:\n{template.Notes.Trim()}";
            await db.AddAuditAsync("VM_REINSTALL_COMPLETED", "vm", vmName, $"Direct reinstall, template {template.Name}, new disk {result.NewDiskPath}.{profileMessage}");

            await NotifyAssignedUsersAsync(vmName,
                $"PenguinPanel: {vmName} reinstall completed",
                $"The virtual server {vmName} was reinstalled from template {template.Name}. Current state: {result.FinalState}.{profileMessage}{templateNotesMessage}");

            return result with { Message = result.Message + profileMessage };
        }
        catch (Exception ex)
        {
            await db.AddAuditAsync("VM_REINSTALL_FAILED", "vm", vmName, $"Direct reinstall, template {template.Name}, error: {ex.Message}");
            throw;
        }
    }

    public async Task DeleteVmTemplateAsync(PanelUser? currentUser, long templateId)
    {
        RequireAdmin(currentUser);
        await db.DeleteVmTemplateAsync(templateId);
        await db.AddAuditAsync("VM_TEMPLATE_DELETED", "template", templateId.ToString(), $"By {currentUser!.Username}");
    }

    public async Task RequestReinstallAsync(PanelUser? currentUser, string vmName, long templateId, string notes)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanRequestReinstall, "Reinstall requests are not enabled for your account.");
        var templates = await db.GetVmTemplatesAsync(enabledOnly: true);
        var template = templates.FirstOrDefault(item => item.Id == templateId)
            ?? throw new InvalidOperationException("Template was not found or is disabled.");

        var requestId = await db.CreateReinstallRequestAsync(currentUser!.Id, vmName, templateId, notes);
        await db.AddAuditAsync("VM_REINSTALL_REQUESTED", "vm", vmName, $"Template: {template.Name}, request #{requestId}, by {currentUser.Username}");

        foreach (var adminEmail in await db.GetAdminEmailsAsync())
        {
            await TrySendEmailAsync(adminEmail,
                $"PenguinPanel reinstall request - {vmName}",
                $"User {currentUser.Username} requested reinstall for {vmName}.\n\nTemplate: {template.Name}\nRequest ID: {requestId}\nNotes: {notes}");
        }
    }

    public async Task<IReadOnlyList<ReinstallRequest>> GetReinstallRequestsAsync(PanelUser? currentUser, string? vmName = null)
    {
        if (currentUser is null)
        {
            return [];
        }

        if (!currentUser.IsAdmin && !currentUser.EffectivePermissions.CanRequestReinstall)
        {
            return [];
        }

        return currentUser.IsAdmin
            ? await db.GetReinstallRequestsAsync(null, vmName)
            : await db.GetReinstallRequestsAsync(currentUser.Id, vmName);
    }

    public async Task UpdateReinstallRequestStatusAsync(PanelUser? currentUser, long requestId, string status)
    {
        RequireAdmin(currentUser);
        status = status.Trim().ToLowerInvariant();
        if (status is not ("pending" or "approved" or "completed" or "rejected" or "failed"))
        {
            throw new InvalidOperationException("Invalid reinstall request status.");
        }

        await db.UpdateReinstallRequestStatusAsync(requestId, status);
        await db.AddAuditAsync("VM_REINSTALL_STATUS_UPDATED", "reinstall", requestId.ToString(), $"Status: {status}, by {currentUser!.Username}");
    }

    public async Task<IReadOnlyList<VmCheckpointInfo>> GetCheckpointsAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");
        return await powerShell.GetVmCheckpointsAsync(vmName);
    }

    public async Task CreateCheckpointAsync(PanelUser? currentUser, string vmName, string checkpointName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");
        checkpointName = string.IsNullOrWhiteSpace(checkpointName)
            ? $"Panel backup {DateTime.Now:yyyy-MM-dd HH-mm}"
            : checkpointName.Trim();

        await powerShell.CreateVmCheckpointAsync(vmName, checkpointName);
        await db.AddAuditAsync("VM_CHECKPOINT_CREATED", "vm", vmName, $"{checkpointName} by {currentUser!.Username}");
    }

    public async Task<CheckpointRestoreResult> RestoreCheckpointAsync(PanelUser? currentUser, string vmName, string checkpointName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");

        var previousState = await powerShell.GetVmStateAsync(vmName);
        var wasRunning = IsRunningLikeState(previousState);

        if (wasRunning)
        {
            await powerShell.StopVmForCheckpointRestoreAsync(vmName);
        }

        await powerShell.RestoreVmCheckpointAsync(vmName, checkpointName);

        var finalState = await powerShell.GetVmStateAsync(vmName);
        if (IsRunningLikeState(finalState))
        {
            await powerShell.StopVmForCheckpointRestoreAsync(vmName);
            finalState = await powerShell.GetVmStateAsync(vmName);
        }

        var details = wasRunning
            ? $"{checkpointName} by {currentUser!.Username}. VM was {previousState}, stopped for restore, and left {finalState}."
            : $"{checkpointName} by {currentUser!.Username}. VM was {previousState} and left {finalState}.";

        await db.AddAuditAsync("VM_CHECKPOINT_RESTORED", "vm", vmName, details);

        var message = wasRunning
            ? $"Checkpoint restored. {vmName} was stopped first and has been left stopped. Start it when you are ready."
            : $"Checkpoint restored. {vmName} is still stopped. Start it when you are ready.";

        return new CheckpointRestoreResult(vmName, checkpointName, wasRunning, previousState, finalState, message);
    }

    public async Task DeleteCheckpointAsync(PanelUser? currentUser, string vmName, string checkpointName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");
        await powerShell.DeleteVmCheckpointAsync(vmName, checkpointName);
        await db.AddAuditAsync("VM_CHECKPOINT_DELETED", "vm", vmName, $"{checkpointName} by {currentUser!.Username}");
    }

    public async Task<string> ScanVmForOutboundAbuseAsync(PanelUser? currentUser, string vmName)
    {
        RequireAdmin(currentUser);
        var result = await ScanVmForOutboundAbuseCoreAsync(vmName, "manual");
        return result.Message;
    }

    public Task<AbuseScanResult> AutoScanVmForOutboundAbuseAsync(string vmName) =>
        ScanVmForOutboundAbuseCoreAsync(vmName, "auto");

    public async Task<string> ScanVmForOutboundAbuseAsync(string vmName)
    {
        var result = await ScanVmForOutboundAbuseCoreAsync(vmName, "system");
        return result.Message;
    }

    private async Task<AbuseScanResult> ScanVmForOutboundAbuseCoreAsync(string vmName, string sampleScope)
    {
        var now = DateTime.UtcNow;
        var config = await db.GetAbuseConfigAsync();
        if (!config.Enabled)
        {
            return new AbuseScanResult(vmName, false, false, 0, "Abuse detection is disabled.", "none", now);
        }

        // Manual and automatic scans must compare against the same meter sample.
        // Older builds kept separate manual/auto baselines, which made the manual scan
        // trigger while the background monitor could appear to do nothing.
        var sampleKey = $"{vmName}::{AbuseMeterSampleScope}";
        var previous = await db.GetPreviousBandwidthSampleAsync(sampleKey);
        var traffic = await powerShell.GetMeteredTrafficAsync(vmName);
        await db.AddBandwidthSampleAsync(sampleKey, traffic.BytesIn, traffic.BytesOut);

        if (previous is null)
        {
            return new AbuseScanResult(vmName, true, false, 0, $"First {sampleScope} sample saved for {vmName}. Scan again after traffic has moved to calculate Mbps.", "baseline", now);
        }

        var elapsedSeconds = Math.Max(1, (now - previous.Value.CreatedAt.ToUniversalTime()).TotalSeconds);
        var bytesDelta = Math.Max(0, traffic.BytesOut - previous.Value.BytesOut);
        var outboundMbps = bytesDelta * 8 / elapsedSeconds / 1_000_000;

        if (outboundMbps < config.OutboundThresholdMbps)
        {
            ResetAbuseState(vmName);
            return new AbuseScanResult(vmName, false, false, outboundMbps, $"No outbound spike detected. Current outbound rate: {outboundMbps:0.###} Mbps.", "none", now);
        }

        var requiredDetections = Math.Clamp(config.RequiredConsecutiveDetections, 1, 20);
        var detectionCount = RecordAbuseHit(vmName, outboundMbps, now);
        if (detectionCount < requiredDetections)
        {
            return new AbuseScanResult(
                vmName,
                false,
                false,
                outboundMbps,
                $"Possible outbound flood detected: {outboundMbps:0.###} Mbps. Waiting for confirmation {detectionCount}/{requiredDetections} before taking action.",
                "pending confirmation",
                now);
        }

        var actions = new List<string> { "alerted" };

        if (config.AutoDisconnect)
        {
            try
            {
                await powerShell.DisconnectVmNetworkAsync(vmName);
                actions.Add("network disconnected");
            }
            catch (Exception ex)
            {
                actions.Add($"network disconnect failed: {ex.Message}");
            }
        }

        if (config.AutoSuspend)
        {
            try
            {
                await powerShell.SuspendVmAsync(vmName);
                actions.Add("vm suspended");
            }
            catch (Exception ex)
            {
                actions.Add($"vm suspend failed: {ex.Message}");
            }
        }

        var action = string.Join(", ", actions);
        await db.AddAbuseEventAsync(vmName, outboundMbps >= config.SustainedThresholdMbps ? "high" : "medium", outboundMbps, action);
        ResetAbuseState(vmName);
        return new AbuseScanResult(vmName, false, true, outboundMbps, $"Confirmed outbound flood detected after {requiredDetections} bad scan(s): {outboundMbps:0.###} Mbps, {action}.", action, now);
    }

    private int RecordAbuseHit(string vmName, double outboundMbps, DateTime now)
    {
        lock (_abuseStateLock)
        {
            if (!_abuseStates.TryGetValue(vmName, out var state))
            {
                state = new AbuseDetectionState();
                _abuseStates[vmName] = state;
            }

            state.ConsecutiveDetections++;
            state.LastDetectedAt = now;
            state.PeakMbps = Math.Max(state.PeakMbps, outboundMbps);
            return state.ConsecutiveDetections;
        }
    }

    private void ResetAbuseState(string vmName)
    {
        lock (_abuseStateLock)
        {
            _abuseStates.Remove(vmName);
        }
    }

    private VmProvisioningDefaults ReadVmProvisioningDefaults()
    {
        var generation = configuration.GetValue("Provisioning:DefaultGeneration", 2) == 1 ? 1 : 2;
        var rdpStart = Math.Clamp(configuration.GetValue("Provisioning:RdpPortStart", 33900), 1024, 65535);
        var rdpEnd = Math.Clamp(configuration.GetValue("Provisioning:RdpPortEnd", 34999), rdpStart, 65535);
        var fiveMStart = Math.Clamp(configuration.GetValue("Provisioning:FiveMPortStart", 35000), 1024, 65535);
        var fiveMEnd = Math.Clamp(configuration.GetValue("Provisioning:FiveMPortEnd", 35999), fiveMStart, 65535);
        var fiveMInternalPort = Math.Clamp(configuration.GetValue("Provisioning:DefaultFiveMInternalPort", 30120), 1, 65535);

        var configuredDefaultRoot = (configuration["Provisioning:VmStorageRoot"] ?? @"D:\Vms").Trim();
        var storageRoots = configuration.GetSection("Provisioning:VmStorageRoots")
            .GetChildren()
            .Select(item => (item.Value ?? string.Empty).Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (storageRoots.Count == 0)
        {
            storageRoots.Add(configuredDefaultRoot);
        }

        var defaultRoot = storageRoots.FirstOrDefault(item => string.Equals(item, configuredDefaultRoot, StringComparison.OrdinalIgnoreCase))
            ?? storageRoots[0];

        return new VmProvisioningDefaults(
            defaultRoot,
            storageRoots,
            (configuration["Provisioning:SwitchName"] ?? "HPanel-NAT").Trim(),
            (configuration["Provisioning:NatName"] ?? configuration["NetworkProfiles:NatName"] ?? "HPanel-NAT").Trim(),
            (configuration["Provisioning:PublicAddress"] ?? string.Empty).Trim(),
            (configuration["Provisioning:InternalIpStart"] ?? "192.168.119.50").Trim(),
            (configuration["Provisioning:InternalIpEnd"] ?? "192.168.119.220").Trim(),
            (configuration["Provisioning:Gateway"] ?? "192.168.119.1").Trim(),
            (configuration["Provisioning:PreferredDnsServer"] ?? "1.1.1.1").Trim(),
            (configuration["Provisioning:AlternateDnsServer"] ?? "1.0.0.1").Trim(),
            rdpStart,
            rdpEnd,
            fiveMStart,
            fiveMEnd,
            fiveMInternalPort,
            generation,
            configuration.GetValue("Provisioning:DefaultSecureBoot", true));
    }

    private async Task EnforceProvisioningResourceLimitsAsync(PanelUser target, int cpu, int memoryMb, int diskGb)
    {
        if (target.IsAdmin)
        {
            return;
        }

        var limits = await db.GetUserResourceLimitsAsync(target.Id);
        if (limits.HasCpuLimit && cpu > limits.MaxCpuCores)
        {
            throw new InvalidOperationException($"Requested CPU ({cpu}) exceeds {target.Username}'s limit ({limits.MaxCpuCores}).");
        }
        if (limits.HasMemoryLimit && memoryMb > limits.MaxMemoryMb)
        {
            throw new InvalidOperationException($"Requested RAM ({memoryMb} MB) exceeds {target.Username}'s limit ({limits.MaxMemoryMb} MB).");
        }
        if (limits.HasDiskLimit && diskGb > limits.MaxDiskGb)
        {
            throw new InvalidOperationException($"Requested disk ({diskGb} GB) exceeds {target.Username}'s limit ({limits.MaxDiskGb} GB).");
        }
    }

    private async Task<string> AllocateProvisioningIpAsync(VmProvisioningDefaults defaults, string? requestedIp)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in await db.GetAllVmNetworkPortProfilesAsync())
        {
            if (!string.IsNullOrWhiteSpace(profile.InternalIpAddress)) used.Add(profile.InternalIpAddress.Trim());
        }
        foreach (var ip in await powerShell.GetUsedProvisioningIpv4AddressesAsync()) used.Add(ip);
        foreach (var ip in ParseDelimitedSetting(configuration["Provisioning:ReservedInternalIps"])) used.Add(ip);

        if (!string.IsNullOrWhiteSpace(requestedIp))
        {
            var normalized = NormalizeIpv4(requestedIp, "Internal IPv4");
            if (used.Contains(normalized))
            {
                throw new InvalidOperationException($"Internal IP {normalized} is already in use or reserved.");
            }
            return normalized;
        }

        var start = ParseIpv4UInt32(defaults.InternalIpStart, "Provisioning InternalIpStart");
        var end = ParseIpv4UInt32(defaults.InternalIpEnd, "Provisioning InternalIpEnd");
        if (end < start)
        {
            throw new InvalidOperationException("Provisioning InternalIpEnd must be greater than or equal to InternalIpStart.");
        }

        for (var current = start; current <= end; current++)
        {
            var candidate = UInt32ToIpv4(current);
            if (!used.Contains(candidate)) return candidate;
            if (current == uint.MaxValue) break;
        }

        throw new InvalidOperationException($"No free internal IP address remains in {defaults.InternalIpStart} - {defaults.InternalIpEnd}.");
    }

    private async Task<int> AllocateProvisioningRdpPortAsync(VmProvisioningDefaults defaults, int? requestedPort)
    {
        var used = new HashSet<int>();
        foreach (var profile in await db.GetAllVmNetworkPortProfilesAsync())
        {
            foreach (var mapping in BuildPortMappings(NormalizeNetworkProfile(profile))) used.Add(mapping.ExternalPort);
        }
        foreach (var port in await powerShell.GetUsedNatExternalPortsAsync(defaults.NatName)) used.Add(port);
        foreach (var port in await powerShell.GetUsedHostListenerPortsAsync()) used.Add(port);
        foreach (var port in ReadReservedExternalPorts()) used.Add(port);

        if (requestedPort is > 0)
        {
            if (requestedPort.Value is < 1024 or > 65535) throw new InvalidOperationException("RDP external port must be between 1024 and 65535.");
            if (used.Contains(requestedPort.Value)) throw new InvalidOperationException($"RDP external port {requestedPort.Value} is already in use.");
            return requestedPort.Value;
        }

        for (var port = defaults.RdpPortStart; port <= defaults.RdpPortEnd; port++)
        {
            if (!used.Contains(port)) return port;
        }

        throw new InvalidOperationException($"No free RDP port remains in {defaults.RdpPortStart}-{defaults.RdpPortEnd}.");
    }

    private async Task<int> AllocateProvisioningFiveMPortAsync(VmProvisioningDefaults defaults, int? requestedPort, int reservedRdpPort)
    {
        var used = new HashSet<int> { reservedRdpPort };
        foreach (var profile in await db.GetAllVmNetworkPortProfilesAsync())
        {
            foreach (var mapping in BuildPortMappings(NormalizeNetworkProfile(profile))) used.Add(mapping.ExternalPort);
        }
        foreach (var port in await powerShell.GetUsedNatExternalPortsAsync(defaults.NatName)) used.Add(port);
        foreach (var port in await powerShell.GetUsedHostListenerPortsAsync()) used.Add(port);
        foreach (var port in ReadReservedExternalPorts()) used.Add(port);

        if (requestedPort is > 0)
        {
            if (requestedPort.Value is < 1024 or > 65535) throw new InvalidOperationException("FiveM external port must be between 1024 and 65535.");
            if (used.Contains(requestedPort.Value)) throw new InvalidOperationException($"FiveM external port {requestedPort.Value} is already in use.");
            return requestedPort.Value;
        }

        for (var port = defaults.FiveMPortStart; port <= defaults.FiveMPortEnd; port++)
        {
            if (!used.Contains(port)) return port;
        }

        throw new InvalidOperationException($"No free FiveM port remains in {defaults.FiveMPortStart}-{defaults.FiveMPortEnd}.");
    }

    private IReadOnlySet<int> ReadReservedExternalPorts()
    {
        var result = new HashSet<int>();
        var raw = configuration["Provisioning:ReservedExternalPorts"] ?? string.Empty;
        foreach (var token in raw.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = token.IndexOf('-');
            if (dash > 0 && dash < token.Length - 1
                && int.TryParse(token[..dash], out var start)
                && int.TryParse(token[(dash + 1)..], out var end))
            {
                start = Math.Clamp(start, 1, 65535);
                end = Math.Clamp(end, 1, 65535);
                if (end < start) (start, end) = (end, start);
                for (var port = start; port <= end; port++)
                {
                    result.Add(port);
                }
                continue;
            }

            if (int.TryParse(token, out var single) && single is >= 1 and <= 65535)
            {
                result.Add(single);
            }
        }

        return result;
    }

    private string ResolveProvisioningStorageRoot(VmProvisioningDefaults defaults, string? requestedRoot, int diskGb)
    {
        var statuses = ReadVmStorageRootStatuses(defaults);
        var reserveGb = ReadMinimumFreeSpaceAfterProvisionGb();
        var reservePercent = ReadMinimumFreeSpaceAfterProvisionPercent();
        var requestedBytes = checked((long)diskGb * 1024L * 1024L * 1024L);

        if (!string.IsNullOrWhiteSpace(requestedRoot))
        {
            var selected = requestedRoot.Trim();
            var status = statuses.FirstOrDefault(item => string.Equals(item.Path, selected, StringComparison.OrdinalIgnoreCase));
            if (status is null)
            {
                throw new InvalidOperationException($"VM storage root '{selected}' is not allowed. Choose one of: {string.Join(", ", defaults.VmStorageRoots)}");
            }

            EnsureStorageRootCanFit(status, diskGb, reserveGb, reservePercent, requestedBytes);
            return status.Path;
        }

        var available = statuses
            .Where(item => item.IsAvailable && item.FreeBytes >= requestedBytes + CalculateStorageReserveBytes(item, reserveGb, reservePercent))
            .ToList();
        if (available.Count == 0)
        {
            var summary = string.Join("; ", statuses.Select(item => FormatStorageRootSummary(item, reserveGb, reservePercent)));
            throw new InvalidOperationException(
                $"No approved VM storage root has enough free space for a {diskGb} GB VPS while keeping the configured storage safety reserve. {summary}");
        }

        var mode = (configuration["Provisioning:StorageSelectionMode"] ?? "MostFreeSpace").Trim();
        VmStorageRootStatus selectedRoot;
        if (string.Equals(mode, "DefaultFirst", StringComparison.OrdinalIgnoreCase))
        {
            selectedRoot = available.FirstOrDefault(item => item.IsDefault)
                ?? available.OrderByDescending(item => item.FreeBytes).First();
        }
        else if (string.Equals(mode, "MostFreePercent", StringComparison.OrdinalIgnoreCase))
        {
            selectedRoot = available
                .OrderByDescending(item => item.TotalBytes > 0 ? (double)item.FreeBytes / item.TotalBytes : 0d)
                .ThenByDescending(item => item.FreeBytes)
                .First();
        }
        else
        {
            // MostFreeSpace is the safe default: place a new VPS on the volume with
            // the largest absolute free-space cushion after filtering by reserve.
            selectedRoot = available
                .OrderByDescending(item => item.FreeBytes)
                .ThenByDescending(item => item.IsDefault)
                .First();
        }

        return selectedRoot.Path;
    }

    private IReadOnlyList<VmStorageRootStatus> ReadVmStorageRootStatuses(VmProvisioningDefaults defaults)
    {
        var result = new List<VmStorageRootStatus>();
        foreach (var configuredRoot in defaults.VmStorageRoots)
        {
            var path = (configuredRoot ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var isDefault = string.Equals(path, defaults.VmStorageRoot, StringComparison.OrdinalIgnoreCase);
            try
            {
                var fullPath = Path.GetFullPath(path);
                var volumeRoot = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(volumeRoot))
                {
                    throw new InvalidOperationException("Could not determine the volume for this path.");
                }

                var drive = new DriveInfo(volumeRoot);
                if (!drive.IsReady)
                {
                    result.Add(new VmStorageRootStatus(path, isDefault, false, 0, 0, $"Drive {volumeRoot} is not ready."));
                    continue;
                }

                result.Add(new VmStorageRootStatus(path, isDefault, true, drive.AvailableFreeSpace, drive.TotalSize, string.Empty));
            }
            catch (Exception ex)
            {
                result.Add(new VmStorageRootStatus(path, isDefault, false, 0, 0, ex.Message));
            }
        }

        return result;
    }

    private int ReadMinimumFreeSpaceAfterProvisionGb() =>
        Math.Clamp(configuration.GetValue("Provisioning:MinimumFreeSpaceAfterProvisionGb", 100), 0, 102400);

    private int ReadMinimumFreeSpaceAfterProvisionPercent() =>
        Math.Clamp(configuration.GetValue("Provisioning:MinimumFreeSpaceAfterProvisionPercent", 5), 0, 50);

    private static long CalculateStorageReserveBytes(VmStorageRootStatus status, int reserveGb, int reservePercent)
    {
        var fixedReserve = checked((long)reserveGb * 1024L * 1024L * 1024L);
        var percentReserve = status.TotalBytes > 0 && reservePercent > 0
            ? (long)Math.Ceiling(status.TotalBytes * (reservePercent / 100d))
            : 0L;
        return Math.Max(fixedReserve, percentReserve);
    }

    private static void EnsureStorageRootCanFit(VmStorageRootStatus status, int diskGb, int reserveGb, int reservePercent, long requestedBytes)
    {
        if (!status.IsAvailable)
        {
            throw new InvalidOperationException($"VM storage root '{status.Path}' is not available. {status.Error}");
        }

        var reserveBytes = CalculateStorageReserveBytes(status, reserveGb, reservePercent);
        if (status.FreeBytes < requestedBytes + reserveBytes)
        {
            throw new InvalidOperationException(
                $"VM storage root '{status.Path}' does not have enough free space for a {diskGb} GB VPS and the configured safety reserve. " +
                $"Free now: {FormatBytesAsGb(status.FreeBytes)} GB; required reserve after provisioning: {FormatBytesAsGb(reserveBytes)} GB.");
        }
    }

    private static string FormatStorageRootSummary(VmStorageRootStatus status, int reserveGb, int reservePercent) =>
        status.IsAvailable
            ? $"{status.Path}: {FormatBytesAsGb(status.FreeBytes)} GB free, reserve {FormatBytesAsGb(CalculateStorageReserveBytes(status, reserveGb, reservePercent))} GB"
            : $"{status.Path}: unavailable ({status.Error})";

    private static string FormatBytesAsGb(long bytes) =>
        (bytes / 1024d / 1024d / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsPathInsideStorageRoot(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prefix = fullRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeProvisioningVmName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("VM name is required.");
        if (name.Length > 80) throw new InvalidOperationException("VM name must be 80 characters or fewer.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]*$"))
        {
            throw new InvalidOperationException("VM name may only contain letters, numbers, dot, underscore, and dash, and must start with a letter or number.");
        }
        return name;
    }

    private static string SafeProvisioningFolderName(string? value)
    {
        var cleaned = new string((value ?? "customer").Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "customer" : cleaned;
    }

    private static IEnumerable<string> ParseDelimitedSetting(string? value) =>
        (value ?? string.Empty).Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeIpv4(string value, string label)
    {
        if (!System.Net.IPAddress.TryParse((value ?? string.Empty).Trim(), out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException($"{label} must be a valid IPv4 address.");
        }
        return address.ToString();
    }

    private static uint ParseIpv4UInt32(string value, string label)
    {
        var normalized = NormalizeIpv4(value, label);
        var bytes = System.Net.IPAddress.Parse(normalized).GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static string UInt32ToIpv4(uint value) =>
        $"{(value >> 24) & 255}.{(value >> 16) & 255}.{(value >> 8) & 255}.{value & 255}";

    private async Task EnforceVmAssignmentLimitAsync(PanelUser target, string vmName)
    {
        if (target.IsAdmin)
        {
            return;
        }

        var limits = await db.GetUserResourceLimitsAsync(target.Id);
        if (!limits.HasVmLimit)
        {
            return;
        }

        var assigned = await db.GetAssignedVmNamesAsync(target.Id);
        if (!assigned.Contains(vmName) && assigned.Count >= limits.MaxVmCount)
        {
            throw new InvalidOperationException($"{target.Username} already has the maximum allowed VM count ({limits.MaxVmCount}).");
        }
    }

    private async Task EnforcePowerActionLimitAsync(PanelUser user, string vmName, string action)
    {
        if (user.IsAdmin)
        {
            return;
        }

        var limits = await db.GetUserResourceLimitsAsync(user.Id);
        if (!limits.HasPowerLimit)
        {
            return;
        }

        var recentCount = await db.CountRecentPowerActionsAsync(user.Id, DateTime.UtcNow.AddHours(-1));
        if (recentCount >= limits.MaxPowerActionsPerHour)
        {
            throw new InvalidOperationException($"Power action limit reached. This account can use {limits.MaxPowerActionsPerHour} power action(s) per hour.");
        }
    }

    private async Task<IReadOnlyList<PortMappingConflict>> GetNetworkProfileConflictsAsync(VmNetworkPortProfile profile)
    {
        var currentMappings = BuildPortMappings(profile).ToList();
        var allProfiles = await db.GetAllVmNetworkPortProfilesAsync();
        var conflicts = new List<PortMappingConflict>();

        foreach (var mapping in currentMappings)
        {
            var duplicateInSameProfile = currentMappings
                .Where(item => !ReferenceEquals(item, mapping))
                .Any(item => string.Equals(item.Protocol, mapping.Protocol, StringComparison.OrdinalIgnoreCase) && item.ExternalPort == mapping.ExternalPort);

            if (duplicateInSameProfile)
            {
                conflicts.Add(new PortMappingConflict(profile.VmName, mapping.Protocol, mapping.ExternalPort, profile.VmName, "Duplicate port inside this profile"));
            }

            foreach (var existing in allProfiles)
            {
                if (string.Equals(existing.VmName, profile.VmName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var other in BuildPortMappings(NormalizeNetworkProfile(existing)))
                {
                    if (string.Equals(other.Protocol, mapping.Protocol, StringComparison.OrdinalIgnoreCase) && other.ExternalPort == mapping.ExternalPort)
                    {
                        conflicts.Add(new PortMappingConflict(profile.VmName, mapping.Protocol, mapping.ExternalPort, existing.VmName, "External port is already reserved"));
                    }
                }
            }
        }

        return conflicts
            .GroupBy(item => $"{item.Protocol}:{item.ExternalPort}:{item.ConflictVmName}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Protocol)
            .ThenBy(item => item.ExternalPort)
            .ToList();
    }

    private static IEnumerable<PanelPortMapping> BuildPortMappings(VmNetworkPortProfile profile)
    {
        if (profile.EnableRdp && profile.RdpExternalPort is > 0 && profile.RdpInternalPort > 0)
        {
            yield return new PanelPortMapping("TCP", profile.RdpExternalPort.Value, profile.RdpInternalPort);
        }

        if (profile.EnableFiveM && profile.FiveMExternalPort is > 0 && profile.FiveMInternalPort > 0)
        {
            yield return new PanelPortMapping("TCP", profile.FiveMExternalPort.Value, profile.FiveMInternalPort);
            yield return new PanelPortMapping("UDP", profile.FiveMExternalPort.Value, profile.FiveMInternalPort);
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

    private static IEnumerable<PanelPortMapping> ParseExtraPorts(string? raw, string protocol)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        var parts = raw.Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var pair = part.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pair.Length == 1 && int.TryParse(pair[0], out var samePort) && samePort is >= 1 and <= 65535)
            {
                yield return new PanelPortMapping(protocol, samePort, samePort);
            }
            else if (pair.Length == 2 && int.TryParse(pair[0], out var externalPort) && int.TryParse(pair[1], out var internalPort) && externalPort is >= 1 and <= 65535 && internalPort is >= 1 and <= 65535)
            {
                yield return new PanelPortMapping(protocol, externalPort, internalPort);
            }
        }
    }

    private sealed record PanelPortMapping(string Protocol, int ExternalPort, int InternalPort);

    private async Task<string> QueueGuestDnsUpdateIfConfiguredAsync(PanelUser requester, VmNetworkPortProfile profile, bool requireConfiguredDns = false)
    {
        if (!profile.HasGuestDnsSettings)
        {
            if (requireConfiguredDns)
            {
                throw new InvalidOperationException("Fill Preferred DNS or Alternate DNS before applying guest DNS settings.");
            }

            return string.Empty;
        }

        var settings = await db.GetVmGuestAgentSettingsAsync(profile.VmName);
        if (!settings.IsEnabled || !settings.HasToken)
        {
            if (requireConfiguredDns)
            {
                throw new InvalidOperationException("Guest Agent is not enabled for this VM. Generate/install a Guest Agent token before applying DNS inside the VM.");
            }

            return " DNS settings were saved, but Guest Agent is not enabled so DNS was not applied inside the VM.";
        }

        var payload = new GuestAgentDnsSettingsPayload(
            profile.DnsInterfaceAlias,
            profile.PreferredDnsServer,
            profile.AlternateDnsServer,
            ResetToAutomatic: false);

        var commandId = await db.CreateGuestAgentCommandAsync(profile.VmName, "set-dns-servers", System.Text.Json.JsonSerializer.Serialize(payload));
        var statusText = settings.IsOnline
            ? $" DNS update command #{commandId} queued for the Guest Agent."
            : $" DNS update command #{commandId} queued; it will run when the Guest Agent checks in.";

        await db.AddAuditAsync("VM_GUEST_DNS_UPDATE_QUEUED", "vm", profile.VmName, $"Command #{commandId}, DNS {BuildDnsSummary(profile)}, by {requester.Username}");
        return statusText;
    }

    private static string BuildDnsSummary(VmNetworkPortProfile profile)
    {
        var values = new[] { profile.PreferredDnsServer, profile.AlternateDnsServer }
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim());
        return string.Join(", ", values);
    }

    private async Task UpdateAccessFromNetworkProfileAsync(VmNetworkPortProfile profile)
    {
        if (!profile.EnableRdp || profile.RdpExternalPort is null or <= 0)
        {
            return;
        }

        var access = await db.GetVmAccessInfoAsync(profile.VmName);
        var publicHost = string.IsNullOrWhiteSpace(profile.PublicHostname) ? access.Hostname : profile.PublicHostname.Trim();
        var publicIp = string.IsNullOrWhiteSpace(profile.PublicIpAddress) ? access.IpAddress : profile.PublicIpAddress.Trim();
        var updated = access with
        {
            Hostname = publicHost,
            IpAddress = publicIp,
            RdpPort = profile.RdpExternalPort,
            UpdatedAt = DateTime.UtcNow
        };
        await db.SaveVmAccessInfoAsync(updated);
    }

    private static VmNetworkPortProfile NormalizeNetworkProfile(VmNetworkPortProfile profile)
    {
        return profile with
        {
            NatName = NormalizeNatName(profile.NatName),
            InternalIpAddress = (profile.InternalIpAddress ?? string.Empty).Trim(),
            PublicIpAddress = (profile.PublicIpAddress ?? string.Empty).Trim(),
            PublicHostname = (profile.PublicHostname ?? string.Empty).Trim(),
            RdpInternalPort = NormalizePort(profile.RdpInternalPort, 3389),
            RdpExternalPort = NormalizeNullablePort(profile.RdpExternalPort),
            FiveMInternalPort = NormalizePort(profile.FiveMInternalPort, 30120),
            FiveMExternalPort = NormalizeNullablePort(profile.FiveMExternalPort),
            ExtraTcpPorts = profile.ExtraTcpPorts ?? string.Empty,
            ExtraUdpPorts = profile.ExtraUdpPorts ?? string.Empty,
            DnsInterfaceAlias = (profile.DnsInterfaceAlias ?? string.Empty).Trim(),
            PreferredDnsServer = NormalizeIpAddressOrEmpty(profile.PreferredDnsServer, "Preferred DNS"),
            AlternateDnsServer = NormalizeIpAddressOrEmpty(profile.AlternateDnsServer, "Alternate DNS"),
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static int NormalizePort(int port, int fallback) => port is >= 1 and <= 65535 ? port : fallback;

    private static int? NormalizeNullablePort(int? port) => port is >= 1 and <= 65535 ? port : null;

    private static string NormalizeIpAddressOrEmpty(string? value, string label)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (!System.Net.IPAddress.TryParse(trimmed, out _))
        {
            throw new InvalidOperationException($"{label} must be a valid IPv4 or IPv6 address.");
        }

        return trimmed;
    }

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

    private async Task<IReadOnlyList<VmInfo>> ReadAllVmsAsync()
    {
        try
        {
            return await powerShell.GetVmsAsync();
        }
        catch (Exception ex)
        {
            await db.AddAuditAsync("HYPERV_READ_FAILED", "host", null, ex.Message);
            return [];
        }
    }

    private async Task EnsureCanAccessVmAsync(PanelUser? user, string vmName)
    {
        if (user is null)
        {
            throw new InvalidOperationException("You need to sign in first.");
        }

        if (string.IsNullOrWhiteSpace(vmName))
        {
            throw new InvalidOperationException("No VM selected.");
        }

        if (user.IsAdmin)
        {
            return;
        }

        if (!await db.IsVmAssignedToUserAsync(user.Id, vmName))
        {
            await db.AddAuditAsync("VM_ACCESS_DENIED", "vm", vmName, $"Denied user {user.Username}");
            throw new InvalidOperationException("This VM is not assigned to your account.");
        }
    }

    private static bool IsRunningLikeState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        return state.Contains("Running", StringComparison.OrdinalIgnoreCase)
            || state.Contains("Starting", StringComparison.OrdinalIgnoreCase)
            || state.Contains("Resuming", StringComparison.OrdinalIgnoreCase)
            || state == "2";
    }

    private async Task RunVmActionAsync(string action, string vmName)
    {
        switch (action)
        {
            case "start":
                await powerShell.StartVmAsync(vmName);
                break;
            case "stop":
                await powerShell.StopVmAsync(vmName);
                break;
            case "restart":
                await powerShell.RestartVmAsync(vmName);
                break;
            case "suspend":
                await powerShell.SuspendVmAsync(vmName);
                break;
            case "resume":
                await powerShell.ResumeVmAsync(vmName);
                break;
            case "disconnect":
                await powerShell.DisconnectVmNetworkAsync(vmName);
                break;
            default:
                throw new InvalidOperationException("Unknown VM action.");
        }
    }


    public async Task TestSmtpAsync(PanelUser? currentUser, string to)
    {
        RequireAdmin(currentUser);

        to = (to ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(to))
        {
            throw new InvalidOperationException("Enter an email address for the SMTP test.");
        }

        var body = $"Hello!\n\nThis is a PenguinPanel SMTP test email.\n\nSent by: {currentUser!.Username}\nTime: {DateTime.UtcNow:u}\n\nIf you received this, SMTP is working.";
        await email.SendAsync(to,
            "PenguinPanel SMTP test",
            body,
            BuildPanelEmailHtml(
                "SMTP test",
                "Email delivery is working",
                "This test message was sent from PenguinPanel.",
                [
                    new("Sent by", currentUser.Username),
                    new("Time", DateTime.UtcNow.ToString("u"))
                ],
                "If this arrived in your inbox, SMTP is configured correctly."));
        await db.AddAuditAsync("EMAIL_TEST_SENT", "email", to, $"By {currentUser.Username}");
    }

    private async Task ValidateEmergencyResetCodeAsync(PanelUser currentUser, string vmName, string verificationCode)
    {
        if (!EmergencyResetCodeRequired)
        {
            return;
        }

        verificationCode = (verificationCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(verificationCode))
        {
            throw new InvalidOperationException("Enter the emergency reset verification code from your email before queueing the reset.");
        }

        var key = EmergencyResetCodeKey(currentUser.Id, vmName);
        if (!_emergencyResetCodes.TryGetValue(key, out var state))
        {
            throw new InvalidOperationException("Request a fresh emergency reset code before queueing the reset.");
        }

        if (state.ExpiresAt <= DateTime.UtcNow)
        {
            _emergencyResetCodes.TryRemove(key, out _);
            throw new InvalidOperationException("The emergency reset code has expired. Request a new code.");
        }

        if (!string.Equals(state.Code, verificationCode, StringComparison.Ordinal))
        {
            var attempts = state.Attempts + 1;
            if (attempts >= 5)
            {
                _emergencyResetCodes.TryRemove(key, out _);
                await db.AddAuditAsync("VM_EMERGENCY_RESET_CODE_LOCKED", "vm", vmName, $"Too many wrong code attempts by {currentUser.Username}");
                throw new InvalidOperationException("Too many wrong reset code attempts. Request a new code.");
            }

            _emergencyResetCodes[key] = state with { Attempts = attempts };
            throw new InvalidOperationException("The emergency reset verification code is incorrect.");
        }

        _emergencyResetCodes.TryRemove(key, out _);
        await db.AddAuditAsync("VM_EMERGENCY_RESET_CODE_ACCEPTED", "vm", vmName, $"By {currentUser.Username}");
    }

    private async Task<bool> SendEmergencyPasswordEmailAsync(PanelUser currentUser, string vmName, string username, string newPassword, long commandId, bool agentIsOnline)
    {
        var to = await db.GetUserEmailAsync(currentUser.Id);
        if (string.IsNullOrWhiteSpace(to))
        {
            await db.AddAuditAsync("VM_EMERGENCY_PASSWORD_EMAIL_SKIPPED", "vm", vmName, $"No email address for {currentUser.Username}");
            return false;
        }

        var statusText = agentIsOnline
            ? "The Guest Agent is online, so the reset should run shortly."
            : "The Guest Agent is offline. The password becomes active when the VM boots and the agent checks in.";

        var body = $"Hello {currentUser.Username},\n\nAn emergency Windows/RDP password reset was queued for {vmName}.\n\nVM: {vmName}\nWindows username: {username}\nNew password: {newPassword}\nCommand id: {commandId}\n\n{statusText}\n\nFor security, sign in and change this password again after you regain access.";

        return await TrySendEmailAsync(to,
            $"PenguinPanel emergency VM password - {vmName}",
            body,
            BuildPanelEmailHtml(
                "Emergency password",
                "Your VM password reset was queued",
                $"Hello {currentUser.Username}, this is the new Windows/RDP password for your virtual server.",
                [
                    new("VM", vmName),
                    new("Windows username", username),
                    new("New password", newPassword, true),
                    new("Command id", commandId.ToString()),
                    new("Status", statusText)
                ],
                "Email is convenient but not perfect security. After you regain access, change the Windows password again inside the VM and check for unknown users or RDP sessions.",
                "Open PenguinPanel",
                PanelPublicUrl));
    }

    private static string EmergencyResetCodeKey(long userId, string vmName) => $"{userId}:{(vmName ?? string.Empty).Trim().ToUpperInvariant()}";

    private static string MaskEmail(string email)
    {
        email = (email ?? string.Empty).Trim();
        var at = email.IndexOf('@');
        if (at <= 1)
        {
            return email;
        }

        var name = email[..at];
        var domain = email[(at + 1)..];
        var visible = name.Length <= 2 ? name[0].ToString() : name[..2];
        return $"{visible}{new string('*', Math.Max(2, name.Length - visible.Length))}@{domain}";
    }

    private async Task NotifyAssignedUsersAsync(string vmName, string subject, string body)
    {
        if (!email.IsEnabled)
        {
            return;
        }

        var assignments = await db.GetUserVmAssignmentsAsync();
        foreach (var assignment in assignments.Where(item => string.Equals(item.VmName, vmName, StringComparison.OrdinalIgnoreCase)))
        {
            var userEmail = await db.GetUserEmailAsync(assignment.UserId);
            await TrySendEmailAsync(userEmail, subject, body);
        }
    }

    private async Task<bool> TrySendEmailAsync(string? to, string subject, string body, string? htmlBody = null)
    {
        if (!email.IsEnabled || string.IsNullOrWhiteSpace(to))
        {
            return false;
        }

        try
        {
            await email.SendAsync(to, subject, body, htmlBody ?? BuildGenericPanelEmailHtml(subject, body));
            await db.AddAuditAsync("EMAIL_SENT", "email", to, subject);
            return true;
        }
        catch (Exception ex)
        {
            await db.AddAuditAsync("EMAIL_SEND_FAILED", "email", to, ex.Message);
            return false;
        }
    }

    private static string BuildGenericPanelEmailHtml(string subject, string body)
    {
        return BuildPanelEmailHtml(
            "Panel notification",
            subject.Replace("PenguinPanel:", string.Empty).Trim(),
            LinesToHtml(body),
            [],
            null,
            introIsHtml: true);
    }

    private static string BuildPanelEmailHtml(
        string eyebrow,
        string title,
        string intro,
        IReadOnlyList<EmailRow> rows,
        string? note = null,
        string? actionLabel = null,
        string? actionUrl = null,
        bool introIsHtml = false)
    {
        // Keep this palette aligned with the PenguinHosting WHMCS email templates.
        // Background / header: #081220
        // Card:                #12243d
        // Inner card:          #0d1b2f
        // Border:              #25476f
        // Accent text:         #7dd3fc
        // Muted text:          #9fb4d0 / #637b9d
        // Primary button:      #2563eb
        var safeEyebrow = Html(eyebrow);
        var safeTitle = Html(title);
        var introHtml = introIsHtml ? intro : Html(intro);
        var rowHtml = rows.Count == 0
            ? string.Empty
            : $"""
                <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="width:100%;margin:22px 0;border-collapse:separate;border-spacing:0 10px;">
                    {string.Join("", rows.Select(BuildEmailRowHtml))}
                </table>
                """;
        var noteHtml = string.IsNullOrWhiteSpace(note)
            ? string.Empty
            : $"""
                <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="width:100%;margin-top:18px;background:#0d1b2f;border:1px solid #25476f;border-radius:12px;">
                    <tr>
                        <td style="padding:14px 16px;font-family:Arial,Helvetica,sans-serif;color:#dbeafe;font-size:14px;line-height:1.55;">
                            <div style="margin-bottom:5px;color:#7dd3fc;font-size:12px;font-weight:800;text-transform:uppercase;letter-spacing:.04em;">Information</div>
                            {Html(note)}
                        </td>
                    </tr>
                </table>
                """;
        var actionHtml = string.IsNullOrWhiteSpace(actionLabel) || string.IsNullOrWhiteSpace(actionUrl)
            ? string.Empty
            : $"""
                <div style="text-align:center;margin-top:22px;">
                    <a href="{Html(actionUrl)}" target="_blank" rel="noopener" style="display:inline-block;background:#2563eb;color:#ffffff;text-decoration:none;font-family:Arial,Helvetica,sans-serif;font-size:15px;font-weight:900;padding:13px 24px;border-radius:999px;">
                        {Html(actionLabel)}
                    </a>
                </div>
                <div style="font-family:Arial,Helvetica,sans-serif;font-size:12px;color:#637b9d;text-align:center;margin-top:14px;line-height:1.5;">
                    Om knappen inte fungerar: <a href="{Html(actionUrl)}" style="color:#7dd3fc;text-decoration:none;">{Html(actionUrl)}</a>
                </div>
                """;

        return $$"""
            <!doctype html>
            <html>
            <head>
                <meta name="viewport" content="width=device-width,initial-scale=1">
                <meta http-equiv="Content-Type" content="text/html; charset=UTF-8">
            </head>
            <body style="margin:0;padding:0;background:#081220;font-family:Arial,Helvetica,sans-serif;color:#ffffff;">
                <div style="display:none;max-height:0;overflow:hidden;opacity:0;color:transparent;">{{safeTitle}}</div>
                <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="width:100%;background:#081220;padding:28px 12px;">
                    <tr>
                        <td align="center">
                            <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="width:100%;max-width:680px;border-collapse:collapse;">
                                <tr>
                                    <td style="padding:0 4px 16px 4px;">
                                        <div style="font-family:Arial,Helvetica,sans-serif;font-size:20px;font-weight:900;color:#ffffff;">PenguinHosting</div>
                                        <div style="font-family:Arial,Helvetica,sans-serif;font-size:12px;color:#637b9d;margin-top:4px;">PenguinPanel</div>
                                    </td>
                                </tr>
                                <tr>
                                    <td style="background:#12243d;border:1px solid #25476f;border-radius:16px;overflow:hidden;">
                                        <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="width:100%;border-collapse:collapse;">
                                            <tr>
                                                <td style="padding:22px 24px;background:#081220;border-bottom:1px solid #25476f;">
                                                    <div style="font-family:Arial,Helvetica,sans-serif;font-size:12px;font-weight:900;color:#7dd3fc;text-transform:uppercase;letter-spacing:.06em;">{{safeEyebrow}}</div>
                                                    <div style="font-family:Arial,Helvetica,sans-serif;font-size:26px;line-height:1.2;font-weight:900;color:#ffffff;margin-top:8px;">{{safeTitle}}</div>
                                                </td>
                                            </tr>
                                            <tr>
                                                <td style="padding:24px;">
                                                    <div style="font-family:Arial,Helvetica,sans-serif;font-size:15px;line-height:1.65;color:#dbeafe;">{{introHtml}}</div>
                                                    {{rowHtml}}
                                                    {{actionHtml}}
                                                    {{noteHtml}}
                                                </td>
                                            </tr>
                                        </table>
                                    </td>
                                </tr>
                                <tr>
                                    <td style="padding:16px 4px 0 4px;font-family:Arial,Helvetica,sans-serif;color:#637b9d;font-size:12px;line-height:1.55;">
                                        Detta mejl skickades automatiskt av PenguinPanel hos PenguinHosting.<br>
                                        Om du inte förväntade dig mejlet, kontakta supporten och dela inte lösenord eller verifieringskoder.
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                </table>
            </body>
            </html>
            """;
    }

    private static string BuildEmailRowHtml(EmailRow row)
    {
        var valueStyle = row.IsSecret
            ? "font-family:Consolas,Menlo,monospace;font-size:16px;letter-spacing:.02em;color:#ffffff;background:#081220;border:1px solid #25476f;border-radius:10px;padding:12px 14px;word-break:break-word;"
            : "font-family:Arial,Helvetica,sans-serif;font-size:16px;color:#ffffff;font-weight:700;word-break:break-word;";

        return $"""
            <tr>
                <td style="padding:0;">
                    <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="width:100%;background:#0d1b2f;border:1px solid #25476f;border-radius:12px;">
                        <tr>
                            <td style="padding:13px 14px;">
                                <div style="margin-bottom:6px;color:#7dd3fc;font-family:Arial,Helvetica,sans-serif;font-size:12px;font-weight:800;text-transform:uppercase;letter-spacing:.04em;">{Html(row.Label)}</div>
                                <div style="{valueStyle}">{Html(row.Value)}</div>
                            </td>
                        </tr>
                    </table>
                </td>
            </tr>
            """;
    }

    private static string LinesToHtml(string value)
    {
        var normalized = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        return string.Join("<br>", normalized.Split('\n').Select(Html));
    }

    private static string GenerateFriendlyPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@$%";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(18);
        var chars = bytes.Select(item => alphabet[item % alphabet.Length]).ToArray();
        return new string(chars);
    }

    private string GetPublicServiceHost(string fallbackAddress)
    {
        var configured = (configuration["Provisioning:PublicHostname"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            return fallbackAddress;
        }

        if (Uri.TryCreate(configured, UriKind.Absolute, out var absoluteUri) && !string.IsNullOrWhiteSpace(absoluteUri.Host))
        {
            return absoluteUri.Host;
        }

        configured = configured.Trim().TrimEnd('/');
        var slashIndex = configured.IndexOf('/');
        if (slashIndex >= 0)
        {
            configured = configured[..slashIndex];
        }

        var colonIndex = configured.IndexOf(':');
        if (colonIndex > 0)
        {
            configured = configured[..colonIndex];
        }

        return string.IsNullOrWhiteSpace(configured) ? fallbackAddress : configured;
    }

    private static string NormalizePanelUrl(string url)
    {
        url = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return "https://vps.penguinhosting.host/";
        }

        return url.EndsWith('/') ? url : url + "/";
    }

    private static string Html(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private sealed record EmergencyResetCodeState(string Code, DateTime ExpiresAt, DateTime LastSentAt, int Attempts);

    private sealed record EmailRow(string Label, string Value, bool IsSecret = false);

    private static void RequireEmergencyPasswordResetAccess(PanelUser? user)
    {
        RequireUserPermission(user, user?.EffectivePermissions.CanViewAccess == true, "Access details are not enabled for your account.");

        if (!user!.IsAdmin && user.IsSubUser)
        {
            throw new InvalidOperationException("Emergency password reset is only available to the account owner and administrators.");
        }
    }

    private static void RequireUserPermission(PanelUser? user, bool allowed, string message)
    {
        if (user is null)
        {
            throw new InvalidOperationException("You need to sign in first.");
        }

        if (!user.IsAdmin && !allowed)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireSignedIn(PanelUser? user)
    {
        if (user is null)
        {
            throw new InvalidOperationException("You need to sign in first.");
        }
    }

    private static void RequireClientOwner(PanelUser? user)
    {
        RequireSignedIn(user);
        if (user!.IsAdmin)
        {
            throw new InvalidOperationException("Use the admin User Manager for admin accounts.");
        }

        if (user.IsSubUser)
        {
            throw new InvalidOperationException("Sub-users cannot manage other sub-users.");
        }
    }

    private async Task<PanelUser> RequireOwnedSubUserAsync(PanelUser? currentUser, long subUserId)
    {
        RequireClientOwner(currentUser);
        var target = await db.GetUserByIdAsync(subUserId)
            ?? throw new InvalidOperationException("Sub-user was not found.");

        if (target.ParentUserId != currentUser!.Id)
        {
            throw new InvalidOperationException("That sub-user does not belong to your account.");
        }

        return target;
    }

    private async Task<IReadOnlyList<string>> ValidateClientVmSelectionAsync(PanelUser currentUser, IReadOnlyList<string> vmNames)
    {
        var allowed = await db.GetAssignedVmNamesAsync(currentUser.Id);
        var selected = (vmNames ?? [])
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var vmName in selected)
        {
            if (!allowed.Contains(vmName))
            {
                throw new InvalidOperationException($"You cannot assign {vmName} because it is not assigned to your account.");
            }
        }

        return selected;
    }

    private static UserVmPermissions CapPermissions(UserVmPermissions requested, UserVmPermissions allowed)
    {
        return new UserVmPermissions(
            requested.CanViewConsole && allowed.CanViewConsole,
            requested.CanViewResources && allowed.CanViewResources,
            requested.CanViewAccess && allowed.CanViewAccess,
            requested.CanViewNetwork && allowed.CanViewNetwork,
            requested.CanViewBackups && allowed.CanViewBackups,
            requested.CanRequestReinstall && allowed.CanRequestReinstall,
            requested.CanViewActivity && allowed.CanViewActivity,
            requested.CanViewSettings && allowed.CanViewSettings,
            requested.CanUsePowerActions && allowed.CanUsePowerActions);
    }

    private async Task<VmAccessInfo> RequireGuestFirewallCredentialsAsync(string vmName)
    {
        var access = await db.GetVmAccessInfoAsync(vmName);
        if (string.IsNullOrWhiteSpace(access.Username) || string.IsNullOrWhiteSpace(access.Password))
        {
            throw new InvalidOperationException("Save the Windows username and password on the Access tab first. Windows Firewall management uses Hyper-V PowerShell Direct and does not expose the password to the browser.");
        }

        return access;
    }

    private static void RequireAdmin(PanelUser? user)
    {
        if (user is null)
        {
            throw new InvalidOperationException("You need to sign in first.");
        }

        if (!user.IsAdmin)
        {
            throw new InvalidOperationException("Admin access required.");
        }
    }

    private static void ValidateUsername(string username)
    {
        if (username.Length < 3)
        {
            throw new InvalidOperationException("Username must be at least 3 characters.");
        }

        if (username.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("Username cannot contain spaces.");
        }
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new InvalidOperationException("Password must be at least 8 characters.");
        }
    }

    private static string NormalizeEmail(string emailAddress)
    {
        emailAddress = (emailAddress ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(emailAddress) && (!emailAddress.Contains('@') || emailAddress.Any(char.IsWhiteSpace)))
        {
            throw new InvalidOperationException("Enter a valid email address.");
        }

        return emailAddress;
    }

    private static string NormalizeRole(string role)
    {
        role = role.Trim().ToLowerInvariant();
        return role switch
        {
            "admin" => "admin",
            "user" => "user",
            _ => throw new InvalidOperationException("Role must be user or admin.")
        };
    }

    private sealed class AbuseDetectionState
    {
        public int ConsecutiveDetections { get; set; }
        public double PeakMbps { get; set; }
        public DateTime LastDetectedAt { get; set; }
    }
}
