namespace HyperVPanel.Services;

public sealed class PanelService(AppDatabase db, PowerShellService powerShell, HyperVConsoleService hyperVConsole, EmailService email, IConfiguration configuration, LiveMonitoringService monitoring, MonitoringAlertService monitoringAlerts, VmAvailabilityService availability, VmSelfHealingService selfHealing, PerformanceHistoryService performanceHistory)
{
    public bool IsSmtpEnabled => email.IsEnabled;
    private const string AbuseMeterSampleScope = "meter";
    private readonly object _abuseStateLock = new();
    private readonly Dictionary<string, AbuseDetectionState> _abuseStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EmergencyResetCodeState> _emergencyResetCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _provisioningLock = new(1, 1);
    private readonly SemaphoreSlim _templateValidationLock = new(1, 1);
    public bool RequireValidatedTemplatesForClients =>
        configuration.GetValue("TemplateValidation:RequireValidatedForClients", true);

    public bool AllowClientReinstallRollbackRestore =>
        configuration.GetValue("ReinstallRollback:AllowClientRestore", true);

    private int ReinstallRollbackRetentionHours =>
        Math.Clamp(
            configuration.GetValue("ReinstallRollback:RetentionHours", 24),
            1,
            24 * 30);

    private int ReinstallRollbackMaxRetentionDays =>
        Math.Clamp(
            configuration.GetValue("ReinstallRollback:MaxRetentionDays", 7),
            1,
            30);
    private string PanelPublicUrl => NormalizePanelUrl(configuration["Panel:PublicUrl"] ?? "https://vps.penguinhosting.host/");
    private bool EmergencyResetCodeRequired => configuration.GetValue("Security:EmergencyResetRequireEmailCode", true);
    private int EmergencyResetCodeMinutes => Math.Clamp(configuration.GetValue("Security:EmergencyResetCodeMinutes", 10), 2, 60);
    private int EmergencyResetCodeCooldownSeconds => Math.Clamp(configuration.GetValue("Security:EmergencyResetCodeCooldownSeconds", 60), 15, 600);
    private bool AgentlessRecoveryEnabled => configuration.GetValue("Security:AgentlessRecoveryEnabled", true);
    private string RecoveryAccountUsername => NormalizeRecoveryAccountUsername(configuration["Security:RecoveryAccountUsername"] ?? "PenguinRecovery");
    public Task<HostNodeStatus> GetHostNodeStatusAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return powerShell.GetHostNodeStatusAsync();
    }

    public async Task<LiveMonitoringOverview> GetLiveMonitoringOverviewAsync(PanelUser? currentUser, bool forceRefresh = false)
    {
        RequireAdmin(currentUser);
        return forceRefresh
            ? await monitoring.RefreshNowAsync()
            : await monitoring.GetSnapshotAsync();
    }

    public async Task<VmLiveMonitoringSnapshot?> GetVmLiveMonitoringAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewResources, "Live monitoring is not enabled for your account.");
        var snapshot = await monitoring.GetSnapshotAsync();
        var vmSnapshot = snapshot.Vms.FirstOrDefault(item => string.Equals(item.VmName, vmName, StringComparison.OrdinalIgnoreCase));
        if (vmSnapshot is null && !string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            throw new InvalidOperationException(snapshot.LastError);
        }

        return vmSnapshot;
    }

    public async Task<IReadOnlyList<HostPerformanceHistoryPoint>> GetHostPerformanceHistoryAsync(PanelUser? currentUser, int hours)
    {
        RequireAdmin(currentUser);
        return await db.GetHostPerformanceHistoryAsync(hours);
    }

    public async Task<IReadOnlyList<VmPerformanceHistoryPoint>> GetVmPerformanceHistoryAsync(PanelUser? currentUser, string vmName, int hours)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewResources, "Performance history is not enabled for your account.");
        return await db.GetVmPerformanceHistoryAsync(vmName, hours);
    }

    public async Task<PerformanceHistoryConfig> GetPerformanceHistoryConfigAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await db.GetPerformanceHistoryConfigAsync();
    }

    public async Task SavePerformanceHistoryConfigAsync(PanelUser? currentUser, PerformanceHistoryConfig config)
    {
        RequireAdmin(currentUser);
        await db.SavePerformanceHistoryConfigAsync(config);
        if (config.Enabled) await performanceHistory.CollectNowAsync();
    }

    public PerformanceHistoryRuntimeStatus GetPerformanceHistoryRuntimeStatus(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return performanceHistory.GetStatus();
    }

    public async Task<(int HostDeleted, int VmDeleted)> ClearPerformanceHistoryAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await performanceHistory.ClearNowAsync();
    }

    public async Task<MonitoringAlertConfig> GetMonitoringAlertConfigAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await db.GetMonitoringAlertConfigAsync();
    }

    public async Task SaveMonitoringAlertConfigAsync(PanelUser? currentUser, MonitoringAlertConfig config)
    {
        RequireAdmin(currentUser);
        await db.SaveMonitoringAlertConfigAsync(config);
        await monitoringAlerts.EvaluateNowAsync();
    }

    public async Task<IReadOnlyList<MonitoringAlert>> GetMonitoringAlertsAsync(PanelUser? currentUser, int limit = 200)
    {
        RequireAdmin(currentUser);
        return await db.GetMonitoringAlertsAsync(limit);
    }

    public MonitoringAlertRuntimeStatus GetMonitoringAlertRuntimeStatus(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return monitoringAlerts.GetStatus();
    }

    public async Task EvaluateMonitoringAlertsNowAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        await monitoringAlerts.EvaluateNowAsync();
    }

    public async Task AcknowledgeMonitoringAlertAsync(PanelUser? currentUser, long alertId)
    {
        RequireAdmin(currentUser);
        await db.AcknowledgeMonitoringAlertAsync(alertId, currentUser!.Username);
    }

    public async Task<int> ClearResolvedMonitoringAlertsAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await db.ClearResolvedMonitoringAlertsAsync();
    }

    public async Task<VmAvailabilityConfig> GetVmAvailabilityConfigAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await db.GetVmAvailabilityConfigAsync();
    }

    public async Task SaveVmAvailabilityConfigAsync(PanelUser? currentUser, VmAvailabilityConfig config)
    {
        RequireAdmin(currentUser);
        await db.SaveVmAvailabilityConfigAsync(config);
        await availability.EvaluateNowAsync();
    }

    public VmAvailabilityRuntimeStatus GetVmAvailabilityRuntimeStatus(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return availability.GetStatus();
    }

    public async Task EvaluateVmAvailabilityNowAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        await availability.EvaluateNowAsync();
    }

    public async Task<VmAvailabilityState?> GetVmAvailabilityStateAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        return await db.GetVmAvailabilityStateAsync(vmName);
    }

    public async Task<IReadOnlyList<VmAvailabilityEvent>> GetVmAvailabilityEventsAsync(PanelUser? currentUser, string vmName, int limit = 20)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        return await db.GetVmAvailabilityEventsAsync(vmName, limit);
    }

    public async Task<VmAvailabilityPreference> GetVmAvailabilityPreferenceAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        return await db.GetVmAvailabilityPreferenceAsync(currentUser!.Id, vmName);
    }

    public async Task SaveVmAvailabilityPreferenceAsync(PanelUser? currentUser, VmAvailabilityPreference preference)
    {
        await EnsureCanAccessVmAsync(currentUser, preference.VmName);
        if (preference.UserId != currentUser!.Id)
        {
            throw new InvalidOperationException("Availability preferences can only be changed for your own account.");
        }
        await db.SaveVmAvailabilityPreferenceAsync(preference);
        await db.AddAuditAsync("VM_AVAILABILITY_PREFERENCE_UPDATED", "vm", preference.VmName, $"By {currentUser.Username}");
    }

    public async Task SetVmAvailabilityMaintenanceAsync(PanelUser? currentUser, string vmName, int hours, string reason)
    {
        RequireAdmin(currentUser);
        await EnsureCanAccessVmAsync(currentUser, vmName);
        hours = Math.Clamp(hours, 1, 24 * 30);
        reason = string.IsNullOrWhiteSpace(reason) ? $"Admin maintenance by {currentUser!.Username}" : reason.Trim();
        await availability.MarkPlannedDowntimeAsync(vmName, reason, TimeSpan.FromHours(hours));
    }

    public async Task ClearVmAvailabilityMaintenanceAsync(PanelUser? currentUser, string vmName)
    {
        RequireAdmin(currentUser);
        await EnsureCanAccessVmAsync(currentUser, vmName);
        await availability.ClearPlannedDowntimeAsync(vmName, currentUser!.Username);
    }

    public async Task<VmSelfHealingConfig> GetVmSelfHealingConfigAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await db.GetVmSelfHealingConfigAsync();
    }

    public async Task SaveVmSelfHealingConfigAsync(PanelUser? currentUser, VmSelfHealingConfig config)
    {
        RequireAdmin(currentUser);
        await db.SaveVmSelfHealingConfigAsync(config);
        if (!config.Enabled)
        {
            foreach (var state in (await db.GetVmSelfHealingStatesAsync()).Values
                .Where(item => item.Status is "waiting_confirmation" or "cooldown" or "exhausted" or "detected"))
            {
                var result = $"Automatic recovery was cancelled because {currentUser!.Username} disabled the global self-healing master switch.";
                await db.SaveVmSelfHealingStateAsync(state with { Status = "cancelled", NextAttemptAt = null, LastResult = result, UpdatedAt = DateTime.UtcNow });
                await db.AddVmSelfHealingEventAsync(state.VmName, state.IncidentType, "recovery_cancelled", "suppressed", state.AttemptCount, result);
                await db.AddAuditAsync("VM_SELF_HEALING_RECOVERY_CANCELLED", "vm", state.VmName, result);
            }
        }
        await selfHealing.EvaluateNowAsync();
    }

    public VmSelfHealingRuntimeStatus GetVmSelfHealingRuntimeStatus(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return selfHealing.GetStatus();
    }

    public async Task EvaluateVmSelfHealingNowAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        await selfHealing.EvaluateNowAsync();
    }

    public async Task<VmSelfHealingPreference> GetVmSelfHealingPreferenceAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        return await db.GetVmSelfHealingPreferenceAsync(vmName);
    }

    public async Task SaveVmSelfHealingPreferenceAsync(PanelUser? currentUser, VmSelfHealingPreference preference)
    {
        RequireAdmin(currentUser);
        await EnsureCanAccessVmAsync(currentUser, preference.VmName);
        preference.HoldReason = (preference.HoldReason ?? string.Empty).Trim();
        preference.UpdatedAt = DateTime.UtcNow;
        await db.SaveVmSelfHealingPreferenceAsync(preference);
        await db.AddAuditAsync("VM_SELF_HEALING_PREFERENCE_UPDATED", "vm", preference.VmName, $"By {currentUser!.Username}; enabled={preference.Enabled}; unexpected={preference.RecoverUnexpectedStop}; heartbeat={preference.RecoverUnresponsive}");
        if (!preference.Enabled)
        {
            var state = await db.GetVmSelfHealingStateAsync(preference.VmName);
            if (state is not null && (state.Status is "waiting_confirmation" or "cooldown" or "exhausted" or "detected"))
            {
                var result = $"Automatic recovery was cancelled because {currentUser.Username} disabled this VPS's self-healing opt-in.";
                await db.SaveVmSelfHealingStateAsync(state with { Status = "cancelled", NextAttemptAt = null, LastResult = result, UpdatedAt = DateTime.UtcNow });
                await db.AddVmSelfHealingEventAsync(preference.VmName, state.IncidentType, "recovery_cancelled", "suppressed", state.AttemptCount, result);
                await db.AddAuditAsync("VM_SELF_HEALING_RECOVERY_CANCELLED", "vm", preference.VmName, result);
            }
        }
    }

    public async Task<VmSelfHealingState?> GetVmSelfHealingStateAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        return await db.GetVmSelfHealingStateAsync(vmName);
    }

    public async Task<IReadOnlyList<VmSelfHealingEvent>> GetVmSelfHealingEventsAsync(PanelUser? currentUser, string vmName, int limit = 20)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        return await db.GetVmSelfHealingEventsAsync(vmName, limit);
    }

    public async Task SetVmSelfHealingHoldAsync(PanelUser? currentUser, string vmName, int hours, string reason)
    {
        RequireAdmin(currentUser);
        await EnsureCanAccessVmAsync(currentUser, vmName);
        hours = Math.Clamp(hours, 1, 24 * 365);
        reason = string.IsNullOrWhiteSpace(reason) ? $"Admin safety hold by {currentUser!.Username}" : reason.Trim();
        await selfHealing.SetSafetyHoldAsync(vmName, reason, TimeSpan.FromHours(hours));
    }

    public async Task ClearVmSelfHealingHoldAsync(PanelUser? currentUser, string vmName)
    {
        RequireAdmin(currentUser);
        await EnsureCanAccessVmAsync(currentUser, vmName);
        await db.ClearVmSelfHealingHoldAsync(vmName, currentUser!.Username);
    }

    public async Task ResetVmSelfHealingStateAsync(PanelUser? currentUser, string vmName)
    {
        RequireAdmin(currentUser);
        await EnsureCanAccessVmAsync(currentUser, vmName);
        await db.ResetVmSelfHealingStateAsync(vmName, currentUser!.Username);
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

    public async Task<HyperVConsoleFrame> GetVmConsoleFrameAsync(PanelUser? user, string vmName)
    {
        await EnsureCanAccessVmAsync(user, vmName);
        RequireUserPermission(user, user!.EffectivePermissions.CanViewConsole, "Console access is not enabled for your account.");
        return await hyperVConsole.CaptureFrameAsync(vmName);
    }

    public async Task TypeVmConsoleTextAsync(PanelUser? user, string vmName, string text)
    {
        await EnsureCanAccessVmAsync(user, vmName);
        RequireUserPermission(user, user!.EffectivePermissions.CanViewConsole, "Console access is not enabled for your account.");
        await hyperVConsole.TypeTextAsync(vmName, text);
    }

    public async Task TypeVmConsoleKeyAsync(PanelUser? user, string vmName, uint virtualKeyCode)
    {
        await EnsureCanAccessVmAsync(user, vmName);
        RequireUserPermission(user, user!.EffectivePermissions.CanViewConsole, "Console access is not enabled for your account.");
        await hyperVConsole.TypeKeyAsync(vmName, virtualKeyCode);
    }

    public async Task SendVmConsoleKeyChordAsync(
        PanelUser? user,
        string vmName,
        uint virtualKeyCode,
        bool control,
        bool alt,
        bool shift)
    {
        await EnsureCanAccessVmAsync(user, vmName);
        RequireUserPermission(user, user!.EffectivePermissions.CanViewConsole, "Console access is not enabled for your account.");
        await hyperVConsole.SendKeyChordAsync(vmName, virtualKeyCode, control, alt, shift);
    }

    public async Task ClickVmConsoleAsync(
        PanelUser? user,
        string vmName,
        double normalizedX,
        double normalizedY,
        uint buttonIndex)
    {
        await EnsureCanAccessVmAsync(user, vmName);
        RequireUserPermission(user, user!.EffectivePermissions.CanViewConsole, "Console access is not enabled for your account.");
        await hyperVConsole.ClickAsync(vmName, normalizedX, normalizedY, buttonIndex);
    }

    public async Task SendVmConsoleCtrlAltDeleteAsync(PanelUser? user, string vmName)
    {
        await EnsureCanAccessVmAsync(user, vmName);
        RequireUserPermission(user, user!.EffectivePermissions.CanViewConsole, "Console access is not enabled for your account.");
        await hyperVConsole.SendCtrlAltDeleteAsync(vmName);
        await db.AddAuditAsync(
            "VM_CONSOLE_CTRL_ALT_DELETE",
            "vm",
            vmName,
            $"By {user!.Username}");
    }

    public async Task VmActionAsync(PanelUser? user, string action, string vmName)
    {
        await EnsureCanAccessVmAsync(user, vmName);
        RequireUserPermission(user, user!.EffectivePermissions.CanUsePowerActions, "Power controls are not enabled for your account.");
        await EnforcePowerActionLimitAsync(user!, vmName, action);
        await MarkPowerActionAvailabilityWindowAsync(vmName, action, user!.Username, internalAction: false);
        try
        {
            await RunVmActionAsync(action, vmName);
        }
        catch
        {
            if (action is "stop" or "restart" or "suspend")
            {
                await availability.ClearPlannedDowntimeAsync(vmName, user.Username + " (power action failed)");
            }
            throw;
        }
        await db.AddPowerActionAsync(user!.Id, vmName, action);
        await db.AddAuditAsync($"VM_{action.ToUpperInvariant()}", "vm", vmName, $"By {user!.Username}");

        if (action is "stop" or "restart" or "suspend")
        {
            await NotifyAssignedUsersAsync(vmName, $"PenguinPanel: {vmName} {action}", $"The virtual server {vmName} received action: {action}.\n\nTriggered by: {user.Username}");
        }
    }

    public async Task VmActionAsync(string action, string vmName)
    {
        await MarkPowerActionAvailabilityWindowAsync(vmName, action, "PenguinPanel automation", internalAction: true);
        try
        {
            await RunVmActionAsync(action, vmName);
        }
        catch
        {
            if (action is "stop" or "restart" or "suspend")
            {
                await availability.ClearPlannedDowntimeAsync(vmName, "PenguinPanel automation (power action failed)");
            }
            throw;
        }
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

        var access = await db.GetVmAccessInfoAsync(vmName);
        var customerRdpHost = GetRdpCustomerHost(
            access.IpAddress,
            access.Hostname);

        return access with
        {
            Hostname = customerRdpHost
        };
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
            $"Windows RDP listener and WinNAT aligned to TCP + UDP {targetPort} by {currentUser!.Username}.");

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
        await db.AddAuditAsync("VM_RDP_PASSWORD_CHANGED", "vm", vmName, $"By {requester.Username}");

        var recoveryMessage = string.Empty;
        if (AgentlessRecoveryEnabled)
        {
            var recoveryUsername = RecoveryAccountUsername;
            if (!string.Equals(
                    NormalizeLocalWindowsUsername(updated.Username),
                    NormalizeLocalWindowsUsername(recoveryUsername),
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // The normal password change just proved that `newPassword` works inside
                    // Windows. Use that known-good credential immediately to create/repair
                    // and rotate the private PenguinPanel recovery account. This means a
                    // normal client can make future forgotten-password recovery ready without
                    // installing an agent or asking support to bootstrap the account.
                    var recoveryPassword = GenerateRecoveryPassword();
                    await powerShell.EnsureWindowsRecoveryAccountAsync(
                        vmName,
                        updated.Username,
                        newPassword,
                        recoveryUsername,
                        recoveryPassword);

                    var previousRecovery = await db.GetVmRecoveryAccountAsync(vmName);
                    var now = DateTime.UtcNow;
                    await db.SaveVmRecoveryAccountAsync(new VmRecoveryAccountSecret(
                        vmName,
                        true,
                        recoveryUsername,
                        recoveryPassword,
                        now,
                        previousRecovery.LastUsedAt,
                        string.Empty,
                        now));

                    await db.AddAuditAsync(
                        "VM_AGENTLESS_RECOVERY_REFRESHED_AFTER_PASSWORD_CHANGE",
                        "vm",
                        vmName,
                        $"Agentless recovery was created/repaired and rotated after a successful normal RDP password change by {requester.Username}.");

                    await db.ClearResolvedWhmcsRecoveryWarningByVmNameAsync(vmName);
                    recoveryMessage = " Password recovery is ready for future use.";
                }
                catch (Exception recoveryEx)
                {
                    // The customer password was already changed successfully. Never turn that
                    // successful operation into a failure merely because optional recovery
                    // bootstrap/repair could not complete.
                    await db.SetVmRecoveryAccountErrorAsync(vmName, recoveryUsername, recoveryEx.Message);
                    await db.AddAuditAsync(
                        "VM_AGENTLESS_RECOVERY_REFRESH_AFTER_PASSWORD_CHANGE_FAILED",
                        "vm",
                        vmName,
                        recoveryEx.Message);

                    recoveryMessage = " The Windows password was changed successfully, but automatic password-recovery setup could not be refreshed. You can retry from the Password Reset tab.";
                }
            }
        }

        return "Remote Desktop password changed inside the VM and saved in the panel." + recoveryMessage;
    }


    public async Task<VmRecoveryAccountStatus> GetVmRecoveryAccountStatusAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewAccess, "Access details are not enabled for your account.");

        var recovery = await db.GetVmRecoveryAccountAsync(vmName);
        var ready = AgentlessRecoveryEnabled
            && recovery.IsEnabled
            && !string.IsNullOrWhiteSpace(recovery.Username)
            && !string.IsNullOrWhiteSpace(recovery.Password);

        return new VmRecoveryAccountStatus(
            vmName,
            ready,
            currentUser.IsAdmin ? recovery.Username : string.Empty,
            recovery.LastRotatedAt,
            recovery.LastUsedAt,
            currentUser.IsAdmin ? recovery.LastError : string.Empty);
    }

    public async Task<VmRecoveryAccountStatus> InitializeVmAgentlessRecoveryAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireEmergencyPasswordResetAccess(currentUser);

        if (!AgentlessRecoveryEnabled)
        {
            throw new InvalidOperationException("Agentless recovery is disabled in Security settings.");
        }

        var state = await powerShell.GetVmStateAsync(vmName);
        if (!IsRunningLikeState(state))
        {
            throw new InvalidOperationException($"The VM must be running to initialize agentless recovery. Current state: {state}.");
        }

        var access = await db.GetVmAccessInfoAsync(vmName);
        var existingRecovery = await db.GetVmRecoveryAccountAsync(vmName);

        var bootstrapUsername = !string.IsNullOrWhiteSpace(existingRecovery.Password)
            ? existingRecovery.Username
            : access.Username;
        var bootstrapPassword = !string.IsNullOrWhiteSpace(existingRecovery.Password)
            ? existingRecovery.Password
            : access.Password;

        if (string.IsNullOrWhiteSpace(bootstrapUsername) || string.IsNullOrWhiteSpace(bootstrapPassword))
        {
            throw new InvalidOperationException("Agentless recovery needs either a working existing recovery credential or the current saved Windows/RDP credential for one-time initialization.");
        }

        var recoveryUsername = RecoveryAccountUsername;
        if (string.Equals(
                NormalizeLocalWindowsUsername(access.Username),
                NormalizeLocalWindowsUsername(recoveryUsername),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("RecoveryAccountUsername cannot be the same as the customer's Windows login username.");
        }

        var recoveryPassword = GenerateRecoveryPassword();

        try
        {
            await powerShell.EnsureWindowsRecoveryAccountAsync(
                vmName,
                bootstrapUsername,
                bootstrapPassword,
                recoveryUsername,
                recoveryPassword);

            var now = DateTime.UtcNow;
            await db.SaveVmRecoveryAccountAsync(new VmRecoveryAccountSecret(
                vmName,
                true,
                recoveryUsername,
                recoveryPassword,
                now,
                existingRecovery.LastUsedAt,
                string.Empty,
                now));

            await db.AddAuditAsync(
                "VM_AGENTLESS_RECOVERY_READY",
                "vm",
                vmName,
                $"Recovery account initialized/rotated by {currentUser!.Username}. Password remains encrypted and is never returned to the browser.");

            await db.ClearResolvedWhmcsRecoveryWarningByVmNameAsync(vmName);
        }
        catch (Exception ex)
        {
            await db.SetVmRecoveryAccountErrorAsync(vmName, recoveryUsername, ex.Message);
            await db.AddAuditAsync("VM_AGENTLESS_RECOVERY_FAILED", "vm", vmName, ex.Message);
            throw;
        }

        return await GetVmRecoveryAccountStatusAsync(currentUser, vmName);
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
        var recovery = await db.GetVmRecoveryAccountAsync(vmName);
        var settings = await db.GetVmGuestAgentSettingsAsync(vmName);
        var agentlessReady = AgentlessRecoveryEnabled
            && recovery.IsEnabled
            && !string.IsNullOrWhiteSpace(recovery.Username)
            && !string.IsNullOrWhiteSpace(recovery.Password);
        var legacyAgentReady = settings.IsEnabled && settings.HasToken;

        if (!agentlessReady && !legacyAgentReady)
        {
            throw new InvalidOperationException("Emergency recovery is not initialized for this VM. An administrator must initialize agentless recovery first.");
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

        await ValidateEmergencyResetCodeAsync(requester, vmName, verificationCode);

        var recovery = await db.GetVmRecoveryAccountAsync(vmName);
        var agentlessReady = AgentlessRecoveryEnabled
            && recovery.IsEnabled
            && !string.IsNullOrWhiteSpace(recovery.Username)
            && !string.IsNullOrWhiteSpace(recovery.Password);

        if (agentlessReady)
        {
            var state = await powerShell.GetVmStateAsync(vmName);
            if (!IsRunningLikeState(state))
            {
                throw new InvalidOperationException($"The VM must be running for agentless emergency recovery. Current state: {state}.");
            }

            if (disableOtherLocalAdministrators || disableRemoteDesktopFirewall)
            {
                throw new InvalidOperationException("The two legacy lockdown options require the optional Guest Agent. Agentless recovery intentionally limits itself to password recovery, session logoff, and Remote Desktop service restart.");
            }

            var rotatedRecoveryPassword = GenerateRecoveryPassword();

            try
            {
                var recoveryResult = await powerShell.ResetWindowsGuestPasswordWithRecoveryAsync(
                    vmName,
                    recovery.Username,
                    recovery.Password,
                    username,
                    newPassword,
                    rotatedRecoveryPassword,
                    forceLogoffSessions,
                    restartRemoteDesktopService);

                var now = DateTime.UtcNow;
                await db.SaveVmRecoveryAccountAsync(recovery with
                {
                    IsEnabled = true,
                    Password = rotatedRecoveryPassword,
                    LastRotatedAt = now,
                    LastUsedAt = now,
                    LastError = string.Empty,
                    UpdatedAt = now
                });

                var updated = existing with
                {
                    Username = username,
                    Password = newPassword,
                    UpdatedAt = now
                };
                await db.SaveVmAccessInfoAsync(updated);

                var passwordEmailSent = false;
                if (emailPasswordToCurrentUser)
                {
                    passwordEmailSent = await SendAgentlessEmergencyPasswordEmailAsync(
                        requester,
                        vmName,
                        username,
                        newPassword);
                }

                await db.AddAuditAsync(
                    "VM_AGENTLESS_EMERGENCY_PASSWORD_RESET",
                    "vm",
                    vmName,
                    $"Windows user {username} reset through PowerShell Direct by {requester.Username}. Recovery credential rotated. Password email sent: {passwordEmailSent}");

                await NotifyAssignedUsersAsync(
                    vmName,
                    $"PenguinPanel: Emergency password reset completed - {vmName}",
                    $"An emergency Windows/RDP password reset was completed for {vmName} using agentless Hyper-V recovery. The saved access password has been updated.");

                var message = "Emergency Windows password reset completed through Hyper-V PowerShell Direct. No Guest Agent was required. The private PenguinPanel recovery credential was rotated automatically.";
                if (emailPasswordToCurrentUser)
                {
                    message += passwordEmailSent
                        ? " The new password was emailed to your panel account email."
                        : " The password was changed, but the password email failed. Copy it from the page before leaving.";
                }

                return new EmergencyPasswordResetQueueResult(message, passwordEmailSent);
            }
            catch (Exception ex)
            {
                await db.SetVmRecoveryAccountErrorAsync(vmName, recovery.Username, ex.Message);
                await db.AddAuditAsync("VM_AGENTLESS_EMERGENCY_RESET_FAILED", "vm", vmName, ex.Message);
                throw new InvalidOperationException(
                    "Agentless recovery could not complete through PowerShell Direct. The customer password stored in PenguinPanel was not changed. " + ex.Message);
            }
        }

        var settings = await db.GetVmGuestAgentSettingsAsync(vmName);
        if (!settings.IsEnabled || !settings.HasToken)
        {
            throw new InvalidOperationException("Agentless recovery is not initialized for this VM, and the optional legacy Guest Agent is not available. Ask an administrator to initialize agentless recovery.");
        }

        var payload = new GuestAgentPasswordResetPayload(
            username,
            newPassword,
            forceLogoffSessions,
            restartRemoteDesktopService,
            disableOtherLocalAdministrators,
            disableRemoteDesktopFirewall);

        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);
        var commandId = await db.CreateGuestAgentCommandAsync(vmName, "reset-windows-password", payloadJson);

        var legacyUpdated = existing with
        {
            Username = username,
            Password = newPassword,
            UpdatedAt = DateTime.UtcNow
        };
        await db.SaveVmAccessInfoAsync(legacyUpdated);

        var legacyPasswordEmailSent = false;
        if (emailPasswordToCurrentUser)
        {
            legacyPasswordEmailSent = await SendEmergencyPasswordEmailAsync(requester, vmName, username, newPassword, commandId, settings.IsOnline);
        }

        await db.AddAuditAsync("VM_EMERGENCY_PASSWORD_RESET_QUEUED", "vm", vmName, $"Legacy Guest Agent command #{commandId}, user {username}, by {requester.Username}, password email sent: {legacyPasswordEmailSent}");

        var legacyMessage = settings.IsOnline
            ? $"Legacy Guest Agent emergency reset command #{commandId} queued and should run shortly."
            : $"Legacy Guest Agent emergency reset command #{commandId} queued. It will run when the VM boots and the agent checks in.";

        if (emailPasswordToCurrentUser)
        {
            legacyMessage += legacyPasswordEmailSent
                ? " The new password was emailed to your panel account email."
                : " The reset was queued, but the password email failed. Copy the password from the page before leaving.";
        }

        return new EmergencyPasswordResetQueueResult(legacyMessage, legacyPasswordEmailSent);
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

    public async Task<ConsistencyScanReport> GetConsistencyScanAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);

        var defaults = ReadVmProvisioningDefaults();
        var inventory = await powerShell.GetConsistencyHostInventoryAsync(
            defaults.NatName,
            defaults.VmStorageRoots);

        var profiles = (await db.GetAllVmNetworkPortProfilesAsync())
            .Select(NormalizeNetworkProfile)
            .ToList();
        var whmcsServices = (await db.GetAllWhmcsServicesAsync()).ToList();
        var assignments = (await db.GetUserVmAssignmentsAsync()).ToList();
        var recoveryRecords = (await db.GetAllVmRecoveryInventoryAsync()).ToList();

        var issues = new List<ConsistencyIssue>();
        var issueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIssue(
            string key,
            string severity,
            string category,
            string resource,
            string summary,
            string details,
            string suggestedAction,
            bool safeCleanupCandidate = false)
        {
            if (!issueKeys.Add(key))
            {
                return;
            }

            issues.Add(new ConsistencyIssue(
                key,
                severity,
                category,
                resource,
                summary,
                details,
                suggestedAction,
                safeCleanupCandidate));
        }

        var vmsByName = inventory.Vms
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var profilesByName = profiles
            .GroupBy(item => item.VmName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var recoveryByName = recoveryRecords
            .GroupBy(item => item.VmName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.UpdatedAt).First(), StringComparer.OrdinalIgnoreCase);
        var whmcsByVm = whmcsServices
            .Where(item => !string.IsNullOrWhiteSpace(item.VmName))
            .GroupBy(item => item.VmName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var liveProfiles = profiles
            .Where(item => vmsByName.ContainsKey(item.VmName))
            .ToList();

        static bool IsWhmcsTerminated(WhmcsServiceRecord service) =>
            string.Equals(service.Status, "terminated", StringComparison.OrdinalIgnoreCase);

        static bool IsWhmcsProvisioningError(WhmcsServiceRecord service) =>
            string.Equals(service.Status, "error", StringComparison.OrdinalIgnoreCase);

        static bool IsWhmcsActiveLike(WhmcsServiceRecord service) =>
            !IsWhmcsTerminated(service) && !IsWhmcsProvisioningError(service);

        static string FirewallIdentity(ConsistencyFirewallRule rule) =>
            !string.IsNullOrWhiteSpace(rule.Name) ? rule.Name : rule.DisplayName;

        // VM <-> PenguinPanel profile consistency.
        foreach (var profile in profiles)
        {
            if (vmsByName.ContainsKey(profile.VmName))
            {
                continue;
            }

            whmcsByVm.TryGetValue(profile.VmName, out var relatedWhmcs);
            relatedWhmcs ??= [];

            // If a non-terminated WHMCS service references this missing VM, WHMCS
            // lifecycle logic below owns the root-cause finding. Avoid showing both
            // "profile missing VM" and "WHMCS missing VM" for the same failure.
            if (relatedWhmcs.Any(service => !IsWhmcsTerminated(service)))
            {
                continue;
            }

            var relatedAssignmentCount = assignments.Count(item =>
                string.Equals(item.VmName, profile.VmName, StringComparison.OrdinalIgnoreCase));
            var hasRecoveryRecord = recoveryByName.ContainsKey(profile.VmName);

            var relatedState = new List<string>
            {
                $"Internal IP: {profile.InternalIpAddress}",
                $"RDP: {profile.RdpExternalPort?.ToString() ?? "off"}",
                $"FiveM: {profile.FiveMExternalPort?.ToString() ?? "off"}"
            };
            if (relatedAssignmentCount > 0) relatedState.Add($"Assignments: {relatedAssignmentCount}");
            if (hasRecoveryRecord) relatedState.Add("Recovery record: present");
            if (relatedWhmcs.Any(IsWhmcsTerminated)) relatedState.Add("WHMCS: terminated history present");

            AddIssue(
                $"profile-no-vm:{profile.VmName}",
                "warning",
                "VM / Database",
                profile.VmName,
                "Stale-looking PenguinPanel profile exists, but the Hyper-V VM is missing.",
                string.Join("; ", relatedState) + ".",
                "Confirm the VM was intentionally removed. This profile and its related PenguinPanel-owned resources can be considered for guarded cleanup in Step 3.1.1.",
                safeCleanupCandidate: true);
        }

        foreach (var vm in inventory.Vms)
        {
            if (!profilesByName.ContainsKey(vm.Name))
            {
                AddIssue(
                    $"vm-no-profile:{vm.Name}",
                    "review",
                    "VM / Database",
                    vm.Name,
                    "Hyper-V VM has no PenguinPanel network profile.",
                    "This may be a manually-created/admin VM, so the scanner will not treat it as an orphan automatically.",
                    "Review ownership. If PenguinPanel should manage this VM, create/apply its Network profile; otherwise leave it unmanaged.");
            }

            foreach (var diskPath in vm.DiskPaths)
            {
                if (string.IsNullOrWhiteSpace(diskPath))
                {
                    continue;
                }

                if (!File.Exists(diskPath))
                {
                    AddIssue(
                        $"missing-attached-disk:{vm.Name}:{diskPath}",
                        "critical",
                        "Storage",
                        vm.Name,
                        "Hyper-V references a virtual disk that does not exist.",
                        diskPath,
                        "Do not start/modify storage automatically. Verify the disk path, storage volume, and recent reinstall/restore activity.");
                    continue;
                }

                var approved = defaults.VmStorageRoots.Any(root => IsPathInsideStorageRoot(diskPath, root));
                if (!approved)
                {
                    AddIssue(
                        $"disk-outside-root:{vm.Name}:{diskPath}",
                        "review",
                        "Storage",
                        vm.Name,
                        "Attached VM disk is outside the approved PenguinPanel storage roots.",
                        diskPath,
                        "Review whether this is an intentional manual VM/storage location.");
                }
            }
        }

        // Internal IP duplicate/reserved checks.
        foreach (var group in profiles
                     .Where(item => !string.IsNullOrWhiteSpace(item.InternalIpAddress))
                     .GroupBy(item => item.InternalIpAddress.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var live = group.Where(item => vmsByName.ContainsKey(item.VmName)).ToList();
            var stale = group.Where(item => !vmsByName.ContainsKey(item.VmName)).ToList();

            if (live.Count > 1)
            {
                AddIssue(
                    $"duplicate-ip:{group.Key}",
                    "critical",
                    "Networking",
                    group.Key,
                    "Duplicate internal IPv4 is assigned to multiple live Hyper-V VMs.",
                    $"Live VMs: {string.Join(", ", live.Select(item => item.VmName))}.",
                    "Assign a unique internal IP to each live VM before applying/restarting network mappings.");
            }
            else if (live.Count == 1 && stale.Count > 0)
            {
                AddIssue(
                    $"stale-ip-reservation:{group.Key}",
                    "warning",
                    "Networking",
                    group.Key,
                    "A stale/missing-VM profile still reserves an internal IP used by a live VM.",
                    $"Live VM: {live[0].VmName}; stale profile(s): {string.Join(", ", stale.Select(item => item.VmName))}.",
                    "Review the stale profile. Clearing the stale profile later can prevent allocator confusion.");
            }
        }

        var reservedIps = ParseDelimitedSetting(configuration["Provisioning:ReservedInternalIps"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in liveProfiles.Where(item => !string.IsNullOrWhiteSpace(item.InternalIpAddress)))
        {
            if (reservedIps.Contains(profile.InternalIpAddress.Trim()))
            {
                AddIssue(
                    $"reserved-ip:{profile.VmName}:{profile.InternalIpAddress}",
                    "critical",
                    "Networking",
                    profile.VmName,
                    $"Reserved internal IP {profile.InternalIpAddress} is assigned to a live VM profile.",
                    "ReservedInternalIps is intended for the Hyper-V gateway/panel infrastructure and other addresses PenguinPanel must not allocate.",
                    "Move this VPS to a free address from the configured VM pool.");
            }
        }

        // Expected NAT/firewall state + duplicate public ports.
        var expectedMappings = new List<ConsistencyExpectedMapping>();
        foreach (var profile in liveProfiles)
        {
            expectedMappings.AddRange(BuildConsistencyExpectedMappings(profile));
        }

        foreach (var group in expectedMappings
                     .GroupBy(item => $"{item.Protocol}:{item.ExternalPort}", StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Select(item => item.VmName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            var names = string.Join(", ", group.Select(item => item.VmName).Distinct(StringComparer.OrdinalIgnoreCase));
            var first = group.First();
            AddIssue(
                $"duplicate-port:{first.Protocol}:{first.ExternalPort}",
                "critical",
                "Ports",
                $"{first.Protocol}/{first.ExternalPort}",
                "The same public protocol/port is reserved by multiple VPS profiles.",
                $"VMs: {names}",
                "Reassign the conflicting public port before applying NAT/firewall.");
        }

        var reservedPorts = ReadReservedExternalPorts();
        foreach (var mapping in expectedMappings)
        {
            if (reservedPorts.Contains(mapping.ExternalPort))
            {
                AddIssue(
                    $"reserved-port:{mapping.VmName}:{mapping.Protocol}:{mapping.ExternalPort}",
                    "critical",
                    "Ports",
                    mapping.VmName,
                    $"{mapping.Protocol}/{mapping.ExternalPort} is reserved for host/infrastructure use.",
                    $"Expected mapping: {mapping.ExternalPort} -> {mapping.InternalIpAddress}:{mapping.InternalPort}",
                    "Allocate another public port for this VPS.");
            }
        }

        foreach (var profile in liveProfiles)
        {
            if (profile.EnableRdp && profile.RdpExternalPort is > 0)
            {
                if (profile.RdpExternalPort == 3389)
                {
                    AddIssue(
                        $"public-rdp-3389:{profile.VmName}",
                        "critical",
                        "Ports",
                        profile.VmName,
                        "Public RDP port is 3389 instead of a PenguinPanel 22000-range allocation.",
                        "This is the stale/internal RDP value that previously leaked into WHMCS provisioning.",
                        $"Reallocate RDP inside {defaults.RdpPortStart}-{defaults.RdpPortEnd}.");
                }
                else if (profile.RdpExternalPort < defaults.RdpPortStart || profile.RdpExternalPort > defaults.RdpPortEnd)
                {
                    AddIssue(
                        $"rdp-outside-pool:{profile.VmName}:{profile.RdpExternalPort}",
                        "warning",
                        "Ports",
                        profile.VmName,
                        $"RDP public port {profile.RdpExternalPort} is outside the configured pool.",
                        $"Configured RDP pool: {defaults.RdpPortStart}-{defaults.RdpPortEnd}.",
                        "Review whether this was intentionally assigned; otherwise reallocate from the current pool.");
                }
            }

            if (profile.EnableFiveM && profile.FiveMExternalPort is > 0)
            {
                if (profile.FiveMExternalPort < defaults.FiveMPortStart || profile.FiveMExternalPort > defaults.FiveMPortEnd)
                {
                    AddIssue(
                        $"fivem-outside-pool:{profile.VmName}:{profile.FiveMExternalPort}",
                        "warning",
                        "Ports",
                        profile.VmName,
                        $"FiveM public port {profile.FiveMExternalPort} is outside the configured pool.",
                        $"Configured FiveM pool: {defaults.FiveMPortStart}-{defaults.FiveMPortEnd}.",
                        "Review whether this was intentionally assigned; otherwise move it into the current public FiveM pool.");
                }

            }
        }

        if (!inventory.NatExists && expectedMappings.Count > 0)
        {
            AddIssue(
                $"nat-missing:{defaults.NatName}",
                "critical",
                "WinNAT",
                defaults.NatName,
                "Configured WinNAT object is missing.",
                $"PenguinPanel expects NAT '{defaults.NatName}' and currently has {expectedMappings.Count} expected inbound mapping(s).",
                "Restore/create the configured WinNAT object before repairing individual VPS mappings.");
        }

        var recognizedFirewallRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var expected in expectedMappings)
        {
            var matchingPort = inventory.NatMappings
                .Where(item => string.Equals(item.Protocol, expected.Protocol, StringComparison.OrdinalIgnoreCase)
                    && item.ExternalPort == expected.ExternalPort)
                .ToList();

            if (matchingPort.Count == 0)
            {
                AddIssue(
                    $"nat-missing-map:{expected.VmName}:{expected.Protocol}:{expected.ExternalPort}",
                    "critical",
                    "WinNAT",
                    expected.VmName,
                    $"Expected {expected.Label} WinNAT mapping is missing.",
                    $"{expected.Protocol}/{expected.ExternalPort} -> {expected.InternalIpAddress}:{expected.InternalPort}",
                    "Use the per-VPS Health → Repair action after reviewing this scan.");
            }
            else if (!matchingPort.Any(item =>
                         string.Equals(item.InternalIpAddress, expected.InternalIpAddress, StringComparison.OrdinalIgnoreCase)
                         && item.InternalPort == expected.InternalPort))
            {
                var actual = string.Join("; ", matchingPort.Select(item =>
                    $"{item.Protocol}/{item.ExternalPort} -> {item.InternalIpAddress}:{item.InternalPort}"));
                AddIssue(
                    $"nat-wrong-target:{expected.VmName}:{expected.Protocol}:{expected.ExternalPort}",
                    "critical",
                    "WinNAT",
                    expected.VmName,
                    $"Public {expected.Protocol}/{expected.ExternalPort} points to the wrong internal target.",
                    $"Expected {expected.InternalIpAddress}:{expected.InternalPort}; actual: {actual}.",
                    "Use the per-VPS Health → Repair action after confirming the saved network profile is correct.");
            }

            var expectedRuleName =
                $"[Penguin VPS] {expected.VmName} - {expected.Label} {expected.Protocol} - Public {expected.ExternalPort} -> {expected.InternalIpAddress}:{expected.InternalPort}";

            var exactRule = inventory.ManagedFirewallRules.FirstOrDefault(rule =>
                string.Equals(rule.DisplayName, expectedRuleName, StringComparison.Ordinal));

            var expectedPortText = expected.ExternalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var portProtocolCandidates = inventory.ManagedFirewallRules
                .Where(rule =>
                    string.Equals(rule.Protocol, expected.Protocol, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(rule.LocalPort, expectedPortText, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(rule =>
                    (!string.IsNullOrWhiteSpace(rule.DisplayName)
                        && rule.DisplayName.Contains(expected.VmName, StringComparison.OrdinalIgnoreCase))
                        ? 1
                        : 0)
                .ToList();

            var effectiveRule = exactRule ?? portProtocolCandidates.FirstOrDefault();

            if (effectiveRule is null)
            {
                AddIssue(
                    $"firewall-missing:{expected.VmName}:{expected.Protocol}:{expected.ExternalPort}",
                    "critical",
                    "Windows Firewall",
                    expected.VmName,
                    $"No managed host firewall rule allows expected {expected.Protocol}/{expected.ExternalPort}.",
                    expectedRuleName,
                    "Use the per-VPS Health → Repair action after reviewing this scan.");
            }
            else
            {
                recognizedFirewallRules.Add(FirewallIdentity(effectiveRule));

                var protocolOk = string.Equals(effectiveRule.Protocol, expected.Protocol, StringComparison.OrdinalIgnoreCase);
                var portOk = string.Equals(effectiveRule.LocalPort, expectedPortText, StringComparison.OrdinalIgnoreCase);
                var behaviorOk = effectiveRule.Enabled
                    && string.Equals(effectiveRule.Direction, "Inbound", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(effectiveRule.Action, "Allow", StringComparison.OrdinalIgnoreCase);

                if (!protocolOk || !portOk || !behaviorOk)
                {
                    AddIssue(
                        $"firewall-wrong:{expected.VmName}:{expected.Protocol}:{expected.ExternalPort}",
                        "critical",
                        "Windows Firewall",
                        expected.VmName,
                        $"Host firewall rule exists but does not correctly allow {expected.Protocol}/{expected.ExternalPort}.",
                        $"Rule: {effectiveRule.DisplayName}; protocol={effectiveRule.Protocol}; localPort={effectiveRule.LocalPort}; enabled={effectiveRule.Enabled}; direction={effectiveRule.Direction}; action={effectiveRule.Action}.",
                        "Use the per-VPS Health → Repair action after reviewing this scan.");
                }
                else if (exactRule is null)
                {
                    AddIssue(
                        $"firewall-legacy-name:{expected.VmName}:{expected.Protocol}:{expected.ExternalPort}",
                        "warning",
                        "Windows Firewall",
                        expected.VmName,
                        $"A working {expected.Protocol}/{expected.ExternalPort} host firewall rule exists, but it uses legacy/non-canonical naming.",
                        $"Current rule: {effectiveRule.DisplayName}; expected canonical name: {expectedRuleName}.",
                        "The port is currently allowed. Use the per-VPS Health → Repair action when convenient to normalize the PenguinPanel rule name.");
                }
            }
        }

        // Penguin-owned NAT entries that are no longer represented by a profile.
        var managedPublicPorts = new HashSet<int>();
        for (var port = defaults.RdpPortStart; port <= defaults.RdpPortEnd; port++) managedPublicPorts.Add(port);
        for (var port = defaults.FiveMPortStart; port <= defaults.FiveMPortEnd; port++) managedPublicPorts.Add(port);

        var expectedPortKeys = expectedMappings
            .Select(item => $"{item.Protocol}:{item.ExternalPort}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownInternalIps = profiles
            .Where(item => !string.IsNullOrWhiteSpace(item.InternalIpAddress))
            .Select(item => item.InternalIpAddress.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in inventory.NatMappings)
        {
            var key = $"{mapping.Protocol}:{mapping.ExternalPort}";
            if (expectedPortKeys.Contains(key))
            {
                continue;
            }

            var matchingManagedFirewall = inventory.ManagedFirewallRules.Any(rule =>
                string.Equals(rule.Protocol, mapping.Protocol, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    rule.LocalPort,
                    mapping.ExternalPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.OrdinalIgnoreCase));

            var strongPenguinOwnership =
                knownInternalIps.Contains(mapping.InternalIpAddress)
                || matchingManagedFirewall;

            if (strongPenguinOwnership)
            {
                AddIssue(
                    $"orphan-nat:{mapping.Protocol}:{mapping.ExternalPort}:{mapping.InternalIpAddress}:{mapping.InternalPort}",
                    "warning",
                    "WinNAT",
                    $"{mapping.Protocol}/{mapping.ExternalPort}",
                    "WinNAT mapping is strongly associated with PenguinPanel, but no current live profile expects it.",
                    $"{mapping.Protocol}/{mapping.ExternalPort} -> {mapping.InternalIpAddress}:{mapping.InternalPort}",
                    "This exact mapping can be previewed for guarded Step 3.1.1 Safe Cleanup.",
                    safeCleanupCandidate: true);
            }
            else if (managedPublicPorts.Contains(mapping.ExternalPort))
            {
                AddIssue(
                    $"unowned-pool-nat:{mapping.Protocol}:{mapping.ExternalPort}:{mapping.InternalIpAddress}:{mapping.InternalPort}",
                    "review",
                    "WinNAT",
                    $"{mapping.Protocol}/{mapping.ExternalPort}",
                    "A WinNAT mapping uses a PenguinPanel public-port range, but ownership cannot be proven.",
                    $"{mapping.Protocol}/{mapping.ExternalPort} -> {mapping.InternalIpAddress}:{mapping.InternalPort}",
                    "Review manually. Safe Cleanup will not remove this mapping solely because its port is inside a PenguinPanel range.");
            }
        }

        var expectedFirewallNames = expectedMappings
            .Select(item => $"[Penguin VPS] {item.VmName} - {item.Label} {item.Protocol} - Public {item.ExternalPort} -> {item.InternalIpAddress}:{item.InternalPort}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var rule in inventory.ManagedFirewallRules)
        {
            if (recognizedFirewallRules.Contains(FirewallIdentity(rule))
                || (!string.IsNullOrWhiteSpace(rule.DisplayName)
                    && expectedFirewallNames.Contains(rule.DisplayName)))
            {
                continue;
            }

            AddIssue(
                $"orphan-firewall:{rule.Name}:{rule.DisplayName}",
                "warning",
                "Windows Firewall",
                string.IsNullOrWhiteSpace(rule.DisplayName) ? rule.Name : rule.DisplayName,
                "PenguinPanel-managed host firewall rule is not expected by any current VPS profile.",
                $"Protocol={rule.Protocol}; LocalPort={rule.LocalPort}; Group={rule.Group}; Enabled={rule.Enabled}.",
                "Review ownership first. This is a candidate for Step 3.1.1 Safe Cleanup.",
                safeCleanupCandidate: true);
        }

        // WHMCS lifecycle consistency.
        var activeLikeWhmcs = whmcsServices
            .Where(IsWhmcsActiveLike)
            .ToList();

        foreach (var group in activeLikeWhmcs
                     .Where(item => !string.IsNullOrWhiteSpace(item.VmName))
                     .GroupBy(item => item.VmName, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            AddIssue(
                $"duplicate-whmcs-vm:{group.Key}",
                "critical",
                "WHMCS",
                group.Key,
                "Multiple active-like WHMCS service records point to the same VM.",
                "Service IDs: " + string.Join(", ", group.Select(item => item.ServiceId)),
                "Review the WHMCS service mappings before provisioning, terminating, or resizing this VM.");
        }

        foreach (var service in whmcsServices)
        {
            var vmExists = !string.IsNullOrWhiteSpace(service.VmName) && vmsByName.ContainsKey(service.VmName);

            if (IsWhmcsProvisioningError(service))
            {
                if (vmExists)
                {
                    AddIssue(
                        $"whmcs-error-vm-exists:{service.ServiceId}",
                        "critical",
                        "WHMCS",
                        $"Service #{service.ServiceId}",
                        $"WHMCS provisioning is in error, but VM '{service.VmName}' still exists.",
                        string.IsNullOrWhiteSpace(service.LastError) ? "No WHMCS last_error is recorded." : service.LastError,
                        "Review the partial VM and its NAT/storage state before retrying Create. PenguinPanel should not create a duplicate VM.");
                }
                else
                {
                    AddIssue(
                        $"whmcs-error-vm-missing:{service.ServiceId}",
                        "warning",
                        "WHMCS",
                        $"Service #{service.ServiceId}",
                        "Previous provisioning attempt failed and no Hyper-V VM exists.",
                        string.IsNullOrWhiteSpace(service.LastError) ? "No WHMCS last_error is recorded." : service.LastError,
                        "This is usually expected after a failed Create. Correct the recorded provisioning error, then retry Create if the service should be provisioned.");
                }

                continue;
            }

            if (IsWhmcsTerminated(service))
            {
                if (vmExists)
                {
                    AddIssue(
                        $"whmcs-terminated-vm-exists:{service.ServiceId}",
                        "critical",
                        "WHMCS",
                        $"Service #{service.ServiceId}",
                        $"WHMCS service is terminated, but Hyper-V VM '{service.VmName}' still exists.",
                        "Termination should normally remove the VM and PenguinPanel-managed network resources.",
                        "Review the VM before deleting anything. If it is confirmed stale, Step 3.1.1 can later offer a guarded cleanup.");
                }

                continue;
            }

            if (!vmExists)
            {
                AddIssue(
                    $"whmcs-active-vm-missing:{service.ServiceId}",
                    "critical",
                    "WHMCS",
                    $"Service #{service.ServiceId}",
                    $"WHMCS service is '{service.Status}', but VM '{service.VmName}' is missing.",
                    string.IsNullOrWhiteSpace(service.LastError) ? "No WHMCS last_error is recorded." : service.LastError,
                    "This service is active-like in WHMCS and needs administrator review before recreate/terminate actions.");
                continue;
            }

            if (!profilesByName.ContainsKey(service.VmName))
            {
                AddIssue(
                    $"whmcs-no-profile:{service.ServiceId}",
                    "warning",
                    "WHMCS",
                    $"Service #{service.ServiceId}",
                    $"WHMCS VM '{service.VmName}' exists but has no PenguinPanel network profile.",
                    $"WHMCS status: {service.Status}.",
                    "Review whether provisioning completed all PenguinPanel configuration stages.");
            }
        }

        // Assignments pointing to deleted VMs.
        foreach (var assignment in assignments)
        {
            if (vmsByName.ContainsKey(assignment.VmName))
            {
                continue;
            }

            // A missing profile/WHMCS VM is already represented as a root finding.
            // Don't multiply one deleted/failed VPS into assignment child warnings.
            if (profilesByName.ContainsKey(assignment.VmName)
                || whmcsByVm.ContainsKey(assignment.VmName))
            {
                continue;
            }

            AddIssue(
                $"assignment-missing-vm:{assignment.Id}",
                "warning",
                "Assignments",
                assignment.Username,
                $"User assignment points to missing VM '{assignment.VmName}'.",
                $"Assignment ID: {assignment.Id}.",
                "Confirm the VPS was deleted. This isolated stale assignment can be removed safely after review.",
                safeCleanupCandidate: true);
        }

        // Recovery records.
        foreach (var recovery in recoveryRecords)
        {
            if (vmsByName.ContainsKey(recovery.VmName))
            {
                continue;
            }

            if (profilesByName.ContainsKey(recovery.VmName)
                || whmcsByVm.ContainsKey(recovery.VmName))
            {
                continue;
            }

            AddIssue(
                $"recovery-orphan:{recovery.VmName}",
                "warning",
                "Recovery",
                recovery.VmName,
                "Agentless recovery record exists for a VM that is no longer in Hyper-V.",
                string.IsNullOrWhiteSpace(recovery.LastError) ? "No recovery error recorded." : recovery.LastError,
                "Confirm the VPS was intentionally deleted. This isolated recovery record is a future safe-cleanup candidate.",
                safeCleanupCandidate: true);
        }

        foreach (var vm in inventory.Vms)
        {
            if (!profilesByName.ContainsKey(vm.Name))
            {
                continue;
            }

            if (!recoveryByName.TryGetValue(vm.Name, out var recovery)
                || !recovery.IsEnabled
                || !recovery.HasSecret)
            {
                AddIssue(
                    $"recovery-not-ready:{vm.Name}",
                    "warning",
                    "Recovery",
                    vm.Name,
                    "Agentless password recovery is not ready.",
                    recovery is null
                        ? "No recovery record exists."
                        : (string.IsNullOrWhiteSpace(recovery.LastError) ? "Recovery record is disabled or has no encrypted secret." : recovery.LastError),
                    "Use Password Reset → Initialize / repair password recovery while a working Windows credential is available.");
            }
        }

        // Storage files under approved roots that are not attached to a current VM.
        var attachedDisks = inventory.Vms
            .SelectMany(item => item.DiskPaths)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => Path.GetFullPath(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var disk in inventory.StorageDisks)
        {
            if (string.IsNullOrWhiteSpace(disk.Path))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(disk.Path);
            if (!attachedDisks.Contains(fullPath))
            {
                AddIssue(
                    $"unattached-disk:{fullPath}",
                    "review",
                    "Storage",
                    Path.GetFileName(fullPath),
                    "VHD/VHDX under an approved VM storage root is not attached to a current Hyper-V VM.",
                    $"{fullPath} ({FormatBytesAsGb(disk.SizeBytes)} GB). Reinstall safety copies and manual disks can be legitimate.",
                    "Review the filename/date/related VM before deleting. Step 3.1.0 never removes unattached disks automatically.");
            }
        }

        issues = issues
            .OrderBy(item => ConsistencySeveritySort(item.Severity))
            .ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Resource, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var critical = issues.Count(item => string.Equals(item.Severity, "critical", StringComparison.OrdinalIgnoreCase));
        var warnings = issues.Count(item => string.Equals(item.Severity, "warning", StringComparison.OrdinalIgnoreCase));
        var review = issues.Count(item => string.Equals(item.Severity, "review", StringComparison.OrdinalIgnoreCase));
        var safeCandidates = issues.Count(item => item.SafeCleanupCandidate);

        var overall = critical > 0
            ? "critical"
            : warnings > 0
                ? "warning"
                : review > 0
                    ? "review"
                    : "healthy";

        await db.AddAuditAsync(
            "CONSISTENCY_SCAN",
            "node",
            Environment.MachineName,
            $"Critical={critical}; Warning={warnings}; Review={review}; SafeCleanupCandidates={safeCandidates}; VMs={inventory.Vms.Count}; Profiles={profiles.Count}; WHMCS={whmcsServices.Count}.");

        return new ConsistencyScanReport(
            overall,
            inventory.Vms.Count,
            profiles.Count,
            whmcsServices.Count,
            inventory.NatMappings.Count,
            inventory.ManagedFirewallRules.Count,
            inventory.StorageDisks.Count,
            critical,
            warnings,
            review,
            safeCandidates,
            issues,
            DateTime.UtcNow);
    }

    public async Task<ConsistencyCleanupPlan> GetConsistencySafeCleanupPlanAsync(PanelUser? currentUser)
    {
        RequireAdmin(currentUser);

        var report = await GetConsistencyScanAsync(currentUser);
        var defaults = ReadVmProvisioningDefaults();
        var inventory = await powerShell.GetConsistencyHostInventoryAsync(
            defaults.NatName,
            defaults.VmStorageRoots);

        var actions = new List<ConsistencyCleanupAction>();

        foreach (var issue in report.Issues.Where(item => item.SafeCleanupCandidate))
        {
            if (issue.Key.StartsWith("profile-no-vm:", StringComparison.OrdinalIgnoreCase))
            {
                var vmName = issue.Resource.Trim();
                if (!string.IsNullOrWhiteSpace(vmName))
                {
                    actions.Add(new ConsistencyCleanupAction(
                        issue.Key,
                        "stale-panel-state",
                        issue.Category,
                        vmName,
                        "Remove stale PenguinPanel management state for missing VM",
                        "Removes the stale network profile, saved access record, assignments, recovery record, public-status record, and legacy guest-agent state. WHMCS rows, audit/history data, Hyper-V VMs, VHD/VHDX files and filesystem data are NOT touched.",
                        vmName,
                        string.Empty,
                        0,
                        string.Empty,
                        0,
                        string.Empty,
                        string.Empty,
                        0));
                }

                continue;
            }

            if (issue.Key.StartsWith("orphan-nat:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = issue.Key.Split(':', StringSplitOptions.TrimEntries);
                if (parts.Length == 5
                    && int.TryParse(parts[2], out var externalPort)
                    && int.TryParse(parts[4], out var internalPort))
                {
                    var mapping = inventory.NatMappings.FirstOrDefault(item =>
                        string.Equals(item.Protocol, parts[1], StringComparison.OrdinalIgnoreCase)
                        && item.ExternalPort == externalPort
                        && string.Equals(item.InternalIpAddress, parts[3], StringComparison.OrdinalIgnoreCase)
                        && item.InternalPort == internalPort);

                    if (mapping is not null)
                    {
                        actions.Add(new ConsistencyCleanupAction(
                            issue.Key,
                            "orphan-nat",
                            issue.Category,
                            issue.Resource,
                            "Remove exact orphan PenguinPanel WinNAT mapping",
                            $"{mapping.Protocol}/{mapping.ExternalPort} -> {mapping.InternalIpAddress}:{mapping.InternalPort}. Only this exact mapping is eligible; no port-range sweep is performed.",
                            string.Empty,
                            mapping.Protocol,
                            mapping.ExternalPort,
                            mapping.InternalIpAddress,
                            mapping.InternalPort,
                            string.Empty,
                            string.Empty,
                            0));
                    }
                }

                continue;
            }

            if (issue.Key.StartsWith("orphan-firewall:", StringComparison.OrdinalIgnoreCase))
            {
                var rule = inventory.ManagedFirewallRules.FirstOrDefault(item =>
                    string.Equals(item.DisplayName, issue.Resource, StringComparison.Ordinal)
                    || string.Equals(item.Name, issue.Resource, StringComparison.Ordinal));

                if (rule is not null)
                {
                    actions.Add(new ConsistencyCleanupAction(
                        issue.Key,
                        "orphan-firewall",
                        issue.Category,
                        issue.Resource,
                        "Remove exact orphan PenguinPanel host-firewall rule",
                        $"Rule '{rule.DisplayName}' ({rule.Protocol}/{rule.LocalPort}). Cleanup rechecks the immutable rule Name and PenguinPanel ownership before deletion.",
                        string.Empty,
                        rule.Protocol,
                        0,
                        string.Empty,
                        0,
                        rule.Name,
                        rule.DisplayName,
                        0));
                }

                continue;
            }

            if (issue.Key.StartsWith("assignment-missing-vm:", StringComparison.OrdinalIgnoreCase))
            {
                var idText = issue.Key["assignment-missing-vm:".Length..];
                if (long.TryParse(idText, out var assignmentId))
                {
                    actions.Add(new ConsistencyCleanupAction(
                        issue.Key,
                        "stale-assignment",
                        issue.Category,
                        issue.Resource,
                        "Remove isolated user-to-VM assignment for missing VM",
                        issue.Details,
                        string.Empty,
                        string.Empty,
                        0,
                        string.Empty,
                        0,
                        string.Empty,
                        string.Empty,
                        assignmentId));
                }

                continue;
            }

            if (issue.Key.StartsWith("recovery-orphan:", StringComparison.OrdinalIgnoreCase))
            {
                var vmName = issue.Resource.Trim();
                if (!string.IsNullOrWhiteSpace(vmName))
                {
                    actions.Add(new ConsistencyCleanupAction(
                        issue.Key,
                        "stale-recovery",
                        issue.Category,
                        vmName,
                        "Remove isolated PenguinRecovery database record for missing VM",
                        "Removes only the encrypted recovery-account database row. No Windows/Hyper-V resource is contacted or changed.",
                        vmName,
                        string.Empty,
                        0,
                        string.Empty,
                        0,
                        string.Empty,
                        string.Empty,
                        0));
                }
            }
        }

        return new ConsistencyCleanupPlan(
            actions
                .GroupBy(item => item.ActionKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => CleanupKindSort(item.Kind))
                .ThenBy(item => item.Resource, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            DateTime.UtcNow);
    }

    public async Task<ConsistencyCleanupResult> ExecuteConsistencySafeCleanupAsync(
        PanelUser? currentUser,
        IReadOnlyCollection<string> selectedActionKeys,
        string confirmation)
    {
        RequireAdmin(currentUser);

        if (!string.Equals((confirmation ?? string.Empty).Trim(), "CLEANUP", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Type CLEANUP exactly to confirm Safe Cleanup.");
        }

        var requested = (selectedActionKeys ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requested.Count == 0)
        {
            throw new InvalidOperationException("Select at least one Safe Cleanup action.");
        }

        await _provisioningLock.WaitAsync();
        try
        {
            // Rebuild the plan AFTER taking the provisioning lock. The browser's
            // preview is never authoritative. If anything changed, that action is
            // skipped instead of being executed from stale UI data.
            var freshPlan = await GetConsistencySafeCleanupPlanAsync(currentUser);
            var freshByKey = freshPlan.Actions.ToDictionary(
                item => item.ActionKey,
                StringComparer.OrdinalIgnoreCase);

            var items = new List<ConsistencyCleanupExecutionItem>();
            var applied = 0;
            var skipped = 0;
            var failed = 0;

            await db.AddAuditAsync(
                "CONSISTENCY_SAFE_CLEANUP_STARTED",
                "node",
                Environment.MachineName,
                $"By {currentUser!.Username}. Requested actions: {requested.Count}.");

            foreach (var requestedKey in requested)
            {
                if (!freshByKey.TryGetValue(requestedKey, out var action))
                {
                    skipped++;
                    var skippedMessage = "Skipped because the action is no longer present in the freshly revalidated Safe Cleanup plan.";
                    items.Add(new ConsistencyCleanupExecutionItem(
                        requestedKey,
                        requestedKey,
                        "skipped",
                        skippedMessage));
                    await db.AddAuditAsync(
                        "CONSISTENCY_SAFE_CLEANUP_SKIPPED",
                        "node",
                        Environment.MachineName,
                        requestedKey + ": " + skippedMessage);
                    continue;
                }

                try
                {
                    string resultMessage;
                    switch (action.Kind)
                    {
                        case "orphan-nat":
                        {
                            var removed = await powerShell.RemoveExactNatMappingAsync(
                                ReadVmProvisioningDefaults().NatName,
                                action.Protocol,
                                action.ExternalPort,
                                action.InternalIpAddress,
                                action.InternalPort);
                            resultMessage = removed > 0
                                ? $"Removed exact WinNAT mapping {action.Protocol}/{action.ExternalPort} -> {action.InternalIpAddress}:{action.InternalPort}."
                                : "Exact WinNAT mapping had already disappeared; no change was required.";
                            break;
                        }

                        case "orphan-firewall":
                        {
                            var removed = await powerShell.RemoveManagedHostFirewallRuleAsync(
                                action.FirewallRuleName,
                                action.FirewallDisplayName);
                            resultMessage = removed > 0
                                ? $"Removed PenguinPanel-managed host firewall rule '{action.FirewallDisplayName}'."
                                : "Firewall rule had already disappeared; no change was required.";
                            break;
                        }

                        case "stale-panel-state":
                            await db.DeleteStaleVmManagementStateAsync(action.VmName);
                            resultMessage = $"Removed stale PenguinPanel management state for missing VM '{action.VmName}'. WHMCS/audit/files were preserved.";
                            break;

                        case "stale-assignment":
                            await db.UnassignVmFromUserAsync(action.RecordId);
                            resultMessage = $"Removed stale user-to-VM assignment record #{action.RecordId}.";
                            break;

                        case "stale-recovery":
                            await db.DeleteVmRecoveryAccountAsync(action.VmName);
                            resultMessage = $"Removed stale encrypted recovery record for missing VM '{action.VmName}'.";
                            break;

                        default:
                            throw new InvalidOperationException($"Unsupported Safe Cleanup action kind '{action.Kind}'.");
                    }

                    applied++;
                    items.Add(new ConsistencyCleanupExecutionItem(
                        action.ActionKey,
                        action.Resource,
                        "applied",
                        resultMessage));
                    await db.AddAuditAsync(
                        "CONSISTENCY_SAFE_CLEANUP_APPLIED",
                        action.Category,
                        action.Resource,
                        $"{resultMessage} By {currentUser.Username}.");
                }
                catch (Exception ex)
                {
                    failed++;
                    items.Add(new ConsistencyCleanupExecutionItem(
                        action.ActionKey,
                        action.Resource,
                        "failed",
                        ex.Message));
                    await db.AddAuditAsync(
                        "CONSISTENCY_SAFE_CLEANUP_FAILED",
                        action.Category,
                        action.Resource,
                        ex.Message);
                }
            }

            var report = await GetConsistencyScanAsync(currentUser);

            await db.AddAuditAsync(
                "CONSISTENCY_SAFE_CLEANUP_COMPLETED",
                "node",
                Environment.MachineName,
                $"By {currentUser.Username}. Applied={applied}; Skipped={skipped}; Failed={failed}; RemainingCritical={report.CriticalCount}; RemainingWarning={report.WarningCount}; RemainingReview={report.ReviewCount}.");

            return new ConsistencyCleanupResult(
                applied,
                skipped,
                failed,
                items,
                report,
                DateTime.UtcNow);
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    public async Task<VmHealthReport> GetVmHealthReportAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireAdmin(currentUser);

        var checks = new List<VmHealthCheck>();
        var profile = NormalizeNetworkProfile(await db.GetVmNetworkPortProfileAsync(vmName));
        VmHostInfrastructureHealth? host = null;

        try
        {
            host = await powerShell.GetVmHostInfrastructureHealthAsync(profile);
        }
        catch (Exception ex)
        {
            checks.Add(new VmHealthCheck(
                "host-diagnostics",
                "Hyper-V host diagnostics",
                "critical",
                "Host diagnostics could not complete.",
                ex.Message,
                false));
        }

        if (host is not null)
        {
            checks.Add(new VmHealthCheck(
                "vm",
                "Hyper-V VM",
                host.VmExists ? "healthy" : "critical",
                host.VmExists ? $"Registered in Hyper-V ({host.VmState})." : "VM is missing from Hyper-V.",
                host.VmExists ? $"Current state: {host.VmState}" : "The database/profile exists, but Hyper-V did not return this VM.",
                false));

            if (host.VmExists)
            {
                var running = IsRunningLikeState(host.VmState);
                checks.Add(new VmHealthCheck(
                    "vm-state",
                    "VM state",
                    running ? "healthy" : "warning",
                    running ? "VM is running." : $"VM is currently {host.VmState}.",
                    running ? "Guest-level checks can run." : "PowerShell Direct, RDP and guest-firewall checks may be skipped until the VM is running.",
                    false));
            }

            var storageStatus = "critical";
            var storageSummary = host.DiskExists ? "Virtual disk exists." : "Virtual disk is missing.";
            var storageDetails = string.IsNullOrWhiteSpace(host.DiskPath) ? "No virtual disk path was reported." : host.DiskPath;

            if (host.DiskExists)
            {
                var defaults = ReadVmProvisioningDefaults();
                var roots = ReadVmStorageRootStatuses(defaults);
                var rootStatus = roots.FirstOrDefault(item => IsPathInsideStorageRoot(host.DiskPath, item.Path));
                if (rootStatus is null)
                {
                    storageStatus = "warning";
                    storageSummary = "Virtual disk exists outside the approved PenguinPanel storage roots.";
                }
                else if (!rootStatus.IsAvailable)
                {
                    storageStatus = "warning";
                    storageSummary = $"Storage root {rootStatus.Path} is currently unavailable to PenguinPanel.";
                    storageDetails = rootStatus.Error;
                }
                else
                {
                    var reserve = CalculateStorageReserveBytes(
                        rootStatus,
                        ReadMinimumFreeSpaceAfterProvisionGb(),
                        ReadMinimumFreeSpaceAfterProvisionPercent());

                    if (rootStatus.FreeBytes < reserve)
                    {
                        storageStatus = "warning";
                        storageSummary = "Virtual disk is healthy, but its storage root is below the configured safety reserve.";
                        storageDetails = $"{host.DiskPath} | Free: {FormatBytesAsGb(rootStatus.FreeBytes)} GB | Reserve: {FormatBytesAsGb(reserve)} GB";
                    }
                    else
                    {
                        storageStatus = "healthy";
                        storageSummary = "Virtual disk exists on an approved storage root.";
                        storageDetails = $"{host.DiskPath} | Root: {rootStatus.Path} | Free: {FormatBytesAsGb(rootStatus.FreeBytes)} GB";
                    }
                }
            }

            checks.Add(new VmHealthCheck(
                "storage",
                "VM storage",
                storageStatus,
                storageSummary,
                storageDetails,
                false));

            if (!string.IsNullOrWhiteSpace(profile.InternalIpAddress))
            {
                checks.Add(new VmHealthCheck(
                    "internal-ip",
                    "Internal IPv4",
                    host.InternalIpObserved ? "healthy" : "warning",
                    host.InternalIpObserved
                        ? $"Hyper-V reports {profile.InternalIpAddress} on the VM."
                        : $"Expected {profile.InternalIpAddress}, but Hyper-V integration services did not report it.",
                    host.HyperVReportedIps.Count == 0
                        ? "No guest IP addresses were reported by Get-VMNetworkAdapter. The VM can still be working; use the Network tab if guest networking actually needs repair."
                        : "Reported: " + string.Join(", ", host.HyperVReportedIps),
                    false));
            }

            checks.Add(new VmHealthCheck(
                "nat",
                "WinNAT",
                host.NatExists ? "healthy" : "critical",
                host.NatExists ? $"WinNAT '{host.NatName}' is available." : $"WinNAT '{host.NatName}' was not found.",
                host.NatExists ? "Host NAT object is available for inbound VPS mappings." : "NAT/firewall repair cannot work until the configured WinNAT object exists.",
                host.Ports.Count > 0));

            foreach (var port in host.Ports)
            {
                var display = $"{port.Label} {port.Protocol} {port.ExternalPort} -> {profile.InternalIpAddress}:{port.InternalPort}";

                checks.Add(new VmHealthCheck(
                    $"nat-{port.Label}-{port.Protocol}-{port.ExternalPort}",
                    $"{port.Label} WinNAT ({port.Protocol})",
                    port.NatHealthy ? "healthy" : "critical",
                    port.NatHealthy ? display : $"Missing/wrong mapping: {display}",
                    port.NatDetails,
                    true));

                checks.Add(new VmHealthCheck(
                    $"host-fw-{port.Label}-{port.Protocol}-{port.ExternalPort}",
                    $"{port.Label} host firewall ({port.Protocol})",
                    port.HostFirewallHealthy ? "healthy" : "critical",
                    port.HostFirewallHealthy ? $"Public TCP/UDP firewall path is open on {port.ExternalPort}." : $"Expected host firewall rule for public port {port.ExternalPort} is missing or wrong.",
                    port.HostFirewallDetails,
                    true));
            }
        }

        var access = await db.GetVmAccessInfoAsync(vmName);
        var recovery = await db.GetVmRecoveryAccountAsync(vmName);
        var vmRunning = host is not null && host.VmExists && IsRunningLikeState(host.VmState);

        string? workingUsername = null;
        string? workingPassword = null;
        string workingSource = string.Empty;
        VmRdpListenerStatus? rdpStatus = null;
        var recoveryCredentialVerified = false;
        var powerShellDirectError = string.Empty;

        if (vmRunning)
        {
            if (AgentlessRecoveryEnabled
                && recovery.IsEnabled
                && !string.IsNullOrWhiteSpace(recovery.Username)
                && !string.IsNullOrWhiteSpace(recovery.Password))
            {
                try
                {
                    rdpStatus = await powerShell.GetWindowsGuestRdpListenerStatusAsync(
                        vmName,
                        recovery.Username,
                        recovery.Password);
                    workingUsername = recovery.Username;
                    workingPassword = recovery.Password;
                    workingSource = "PenguinRecovery";
                    recoveryCredentialVerified = true;
                }
                catch (Exception ex)
                {
                    powerShellDirectError = "Recovery credential: " + ex.Message;
                }
            }

            if (workingUsername is null
                && !string.IsNullOrWhiteSpace(access.Username)
                && !string.IsNullOrWhiteSpace(access.Password))
            {
                try
                {
                    rdpStatus = await powerShell.GetWindowsGuestRdpListenerStatusAsync(
                        vmName,
                        access.Username,
                        access.Password);
                    workingUsername = access.Username;
                    workingPassword = access.Password;
                    workingSource = "saved Windows credential";
                }
                catch (Exception ex)
                {
                    powerShellDirectError = string.IsNullOrWhiteSpace(powerShellDirectError)
                        ? ex.Message
                        : powerShellDirectError + " | Saved credential: " + ex.Message;
                }
            }

            checks.Add(new VmHealthCheck(
                "powershell-direct",
                "PowerShell Direct",
                workingUsername is not null ? "healthy" : "warning",
                workingUsername is not null
                    ? $"Hyper-V PowerShell Direct works using the {workingSource}."
                    : "PenguinPanel could not authenticate to Windows through PowerShell Direct.",
                workingUsername is not null
                    ? "No guest network, WinRM or inbound management port is required."
                    : (string.IsNullOrWhiteSpace(powerShellDirectError) ? "No usable saved Windows/recovery credential was available." : powerShellDirectError),
                false));

            if (profile.EnableRdp && profile.RdpInternalPort > 0)
            {
                if (rdpStatus is null)
                {
                    checks.Add(new VmHealthCheck(
                        "rdp-listener",
                        "Windows RDP listener",
                        "warning",
                        "RDP listener could not be inspected.",
                        "PowerShell Direct was not available with a known credential.",
                        true));
                }
                else
                {
                    var portMatches = rdpStatus.Port == profile.RdpInternalPort;
                    var tcpHealthy = rdpStatus.IsReady && portMatches;
                    var udpHealthy = rdpStatus.IsUdpReady && portMatches;
                    checks.Add(new VmHealthCheck(
                        "rdp-listener",
                        "Windows RDP listener",
                        tcpHealthy ? (udpHealthy ? "healthy" : "warning") : "critical",
                        tcpHealthy
                            ? udpHealthy
                                ? $"Windows RDP is listening correctly on TCP + UDP {rdpStatus.Port}."
                                : $"Windows RDP TCP {rdpStatus.Port} is ready, but its UDP transport is not listening. TCP fallback still works."
                            : $"Expected Windows RDP TCP + UDP {profile.RdpInternalPort}; Windows reports port {rdpStatus.Port}.",
                        $"Remote Desktop enabled: {rdpStatus.RemoteDesktopEnabled}; TermService running: {rdpStatus.TermServiceRunning}; TCP listening: {rdpStatus.TcpListening}; UDP listening: {rdpStatus.UdpListening}.",
                        true));
                }
            }

            if (workingUsername is not null && workingPassword is not null && host is not null)
            {
                try
                {
                    var guestRules = await powerShell.GetWindowsGuestFirewallRulesAsync(vmName, workingUsername, workingPassword);
                    foreach (var expected in host.Ports
                                 .GroupBy(item => (item.Protocol.ToUpperInvariant(), item.InternalPort, item.Label))
                                 .Select(group => group.First()))
                    {
                        var expectedPort = expected.InternalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var healthy = guestRules.Any(rule =>
                            rule.IsAutomatic
                            && rule.Enabled
                            && string.Equals(rule.Action, "Allow", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(rule.Protocol, expected.Protocol, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(rule.LocalPort, expectedPort, StringComparison.OrdinalIgnoreCase));

                        checks.Add(new VmHealthCheck(
                            $"guest-fw-{expected.Label}-{expected.Protocol}-{expected.InternalPort}",
                            $"{expected.Label} guest firewall ({expected.Protocol})",
                            healthy ? "healthy" : "critical",
                            healthy
                                ? $"Windows guest firewall allows local {expected.Protocol}/{expected.InternalPort}."
                                : $"Windows guest firewall is missing the automatic {expected.Protocol}/{expected.InternalPort} rule.",
                            $"Expected inside VM: [Penguin VPS] {vmName} - {expected.Label} {expected.Protocol} - Local {expected.InternalPort}",
                            true));
                    }
                }
                catch (Exception ex)
                {
                    checks.Add(new VmHealthCheck(
                        "guest-firewall",
                        "Windows guest firewall",
                        "warning",
                        "Guest firewall rules could not be inspected.",
                        ex.Message,
                        true));
                }

                if (profile.HasGuestDnsSettings)
                {
                    try
                    {
                        var dns = await powerShell.GetWindowsGuestDnsStatusAsync(
                            vmName,
                            workingUsername,
                            workingPassword,
                            profile.DnsInterfaceAlias);

                        var expectedDns = new[] {
                                (profile.PreferredDnsServer ?? string.Empty).Trim(),
                                (profile.AlternateDnsServer ?? string.Empty).Trim()
                            }
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        var actualDns = dns.DnsServers
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        var exactMatch = expectedDns.SequenceEqual(
                            actualDns,
                            StringComparer.OrdinalIgnoreCase);

                        var preferredMatches = expectedDns.Count == 0
                            || (actualDns.Count > 0
                                && string.Equals(
                                    expectedDns[0],
                                    actualDns[0],
                                    StringComparison.OrdinalIgnoreCase));

                        var status = exactMatch
                            ? "healthy"
                            : preferredMatches
                                ? "warning"
                                : "critical";

                        var expectedText = expectedDns.Count == 0
                            ? "not configured"
                            : string.Join(", ", expectedDns);
                        var actualText = actualDns.Count == 0
                            ? "none"
                            : string.Join(", ", actualDns);

                        var summary = exactMatch
                            ? $"Windows guest DNS matches PenguinPanel: {actualText}."
                            : $"DNS drift detected. Expected {expectedText}; Windows uses {actualText}.";

                        var details = !exactMatch && preferredMatches && actualDns.Count > expectedDns.Count
                            ? "The preferred resolver is correct, but Windows has extra resolver(s). If the preferred DNS becomes unavailable, an extra public resolver can bypass DNS filtering/policy."
                            : $"Interface: {dns.InterfaceAlias}. PenguinPanel desired DNS: {expectedText}. Windows DNS: {actualText}.";

                        checks.Add(new VmHealthCheck(
                            "guest-dns",
                            "Windows guest DNS",
                            status,
                            summary,
                            details,
                            true));
                    }
                    catch (Exception ex)
                    {
                        checks.Add(new VmHealthCheck(
                            "guest-dns",
                            "Windows guest DNS",
                            "warning",
                            "Guest DNS settings could not be inspected.",
                            ex.Message,
                            true));
                    }
                }
            }
        }
        else
        {
            checks.Add(new VmHealthCheck(
                "powershell-direct",
                "PowerShell Direct",
                "skipped",
                "Live guest checks skipped because the VM is not running.",
                "Start the VM and run diagnosis again to inspect RDP and the Windows guest firewall.",
                false));
        }

        var recoveryConfigured = AgentlessRecoveryEnabled
            && recovery.IsEnabled
            && !string.IsNullOrWhiteSpace(recovery.Username)
            && !string.IsNullOrWhiteSpace(recovery.Password);

        var recoveryStatus = recoveryConfigured
            ? (vmRunning && !recoveryCredentialVerified && workingUsername is not null ? "warning" : "healthy")
            : "warning";

        var recoverySummary = !recoveryConfigured
            ? "Agentless password recovery is not initialized."
            : recoveryCredentialVerified
                ? "PenguinRecovery credential was verified through PowerShell Direct."
                : vmRunning
                    ? "Recovery is configured, but its credential was not verified during this diagnosis."
                    : "Recovery is configured; live verification is skipped while the VM is stopped.";

        checks.Add(new VmHealthCheck(
            "recovery",
            "Password recovery",
            recoveryStatus,
            recoverySummary,
            string.IsNullOrWhiteSpace(recovery.LastError)
                ? "The recovery secret stays encrypted in PenguinPanel and is never shown to the customer."
                : "Last recovery error: " + recovery.LastError,
            true));

        var overall = checks.Any(item => string.Equals(item.Status, "critical", StringComparison.OrdinalIgnoreCase))
            ? "critical"
            : checks.Any(item => string.Equals(item.Status, "warning", StringComparison.OrdinalIgnoreCase))
                ? "warning"
                : "healthy";

        return new VmHealthReport(vmName, overall, checks, DateTime.UtcNow);
    }

    public async Task<VmHealthRepairResult> RepairVmHealthAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireAdmin(currentUser);

        var actions = new List<string>();
        var warnings = new List<string>();
        var profile = NormalizeNetworkProfile(await db.GetVmNetworkPortProfileAsync(vmName));
        var host = await powerShell.GetVmHostInfrastructureHealthAsync(profile);

        if (!host.VmExists)
        {
            throw new InvalidOperationException("Health repair cannot run because the Hyper-V VM is missing.");
        }

        if (profile.HasRdpRule
            || profile.HasFiveMRule
            || !string.IsNullOrWhiteSpace(profile.ExtraTcpPorts)
            || !string.IsNullOrWhiteSpace(profile.ExtraUdpPorts))
        {
            try
            {
                var applied = await powerShell.ApplyNetworkPortProfileAsync(profile);
                actions.Add("Re-applied and verified host WinNAT + Windows Firewall rules. " + applied.Message);
            }
            catch (Exception ex)
            {
                warnings.Add("Host NAT/firewall repair failed: " + ex.Message);
            }
        }

        if (IsRunningLikeState(host.VmState))
        {
            var access = await db.GetVmAccessInfoAsync(vmName);
            var recovery = await db.GetVmRecoveryAccountAsync(vmName);

            string? username = null;
            string? password = null;
            var credentialSource = string.Empty;

            if (AgentlessRecoveryEnabled
                && recovery.IsEnabled
                && !string.IsNullOrWhiteSpace(recovery.Username)
                && !string.IsNullOrWhiteSpace(recovery.Password))
            {
                try
                {
                    await powerShell.GetWindowsGuestRdpListenerStatusAsync(vmName, recovery.Username, recovery.Password);
                    username = recovery.Username;
                    password = recovery.Password;
                    credentialSource = "PenguinRecovery";
                }
                catch
                {
                    // Fall back to the customer's saved credential below.
                }
            }

            if (username is null
                && !string.IsNullOrWhiteSpace(access.Username)
                && !string.IsNullOrWhiteSpace(access.Password))
            {
                try
                {
                    await powerShell.GetWindowsGuestRdpListenerStatusAsync(vmName, access.Username, access.Password);
                    username = access.Username;
                    password = access.Password;
                    credentialSource = "saved Windows credential";
                }
                catch (Exception ex)
                {
                    warnings.Add("Guest repair skipped because neither the recovery credential nor saved Windows credential could authenticate through PowerShell Direct: " + ex.Message);
                }
            }

            if (username is not null && password is not null)
            {
                if (profile.EnableRdp && profile.RdpInternalPort > 0)
                {
                    try
                    {
                        var rdp = await powerShell.SetWindowsGuestRdpPortAsync(
                            vmName,
                            username,
                            password,
                            profile.RdpInternalPort);
                        actions.Add($"Verified/repaired Windows RDP listener and TCP + UDP firewall on port {rdp.Port} using {credentialSource}.");
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Windows RDP listener repair failed: " + ex.Message);
                    }
                }

                try
                {
                    var guestFirewall = await powerShell.SyncWindowsGuestFirewallRulesAsync(
                        profile,
                        username,
                        password);
                    actions.Add(guestFirewall.Message);
                }
                catch (Exception ex)
                {
                    warnings.Add("Windows guest firewall repair failed: " + ex.Message);
                }

                if (profile.HasGuestDnsSettings)
                {
                    try
                    {
                        var dns = await powerShell.SyncWindowsGuestDnsServersAsync(
                            profile,
                            username,
                            password);
                        actions.Add(
                            "Synced Windows guest DNS to the saved PenguinPanel profile: "
                            + string.Join(", ", dns.DnsServers)
                            + ".");
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Windows guest DNS repair failed: " + ex.Message);
                    }
                }

                try
                {
                    var recoveryUsername = RecoveryAccountUsername;
                    if (!string.Equals(
                            NormalizeLocalWindowsUsername(access.Username),
                            NormalizeLocalWindowsUsername(recoveryUsername),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var newRecoveryPassword = GenerateRecoveryPassword();
                        await powerShell.EnsureWindowsRecoveryAccountAsync(
                            vmName,
                            username,
                            password,
                            recoveryUsername,
                            newRecoveryPassword);

                        var now = DateTime.UtcNow;
                        await db.SaveVmRecoveryAccountAsync(new VmRecoveryAccountSecret(
                            vmName,
                            true,
                            recoveryUsername,
                            newRecoveryPassword,
                            now,
                            recovery.LastUsedAt,
                            string.Empty,
                            now));

                        await db.ClearResolvedWhmcsRecoveryWarningByVmNameAsync(vmName);
                        actions.Add("Created/repaired and rotated the private PenguinRecovery credential.");
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("Password recovery repair failed: " + ex.Message);
                    await db.SetVmRecoveryAccountErrorAsync(vmName, RecoveryAccountUsername, ex.Message);
                }
            }
        }
        else
        {
            warnings.Add($"Guest-level repair was skipped because the VM is {host.VmState}. Host NAT/firewall repair can still run while the guest is stopped.");
        }

        await db.AddAuditAsync(
            "VM_HEALTH_REPAIR",
            "vm",
            vmName,
            $"By {currentUser!.Username}. Actions: {actions.Count}. Warnings: {warnings.Count}. {string.Join(" | ", actions.Concat(warnings))}");

        var report = await GetVmHealthReportAsync(currentUser, vmName);
        var message = warnings.Count == 0
            ? $"Health repair completed with {actions.Count} repair action(s)."
            : $"Health repair completed with {actions.Count} action(s) and {warnings.Count} warning(s).";

        return new VmHealthRepairResult(vmName, actions.Count, actions, warnings, report, message);
    }

    public async Task<VmNetworkPortProfile> GetVmNetworkPortProfileAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireAdmin(currentUser);
        return NormalizeNetworkProfile(await db.GetVmNetworkPortProfileAsync(vmName));
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
        var rawProfile = await db.GetVmNetworkPortProfileAsync(vmName);
        var profile = NormalizeNetworkProfile(rawProfile);
        if (rawProfile.FiveMInternalPort != profile.FiveMInternalPort)
        {
            await db.SaveVmNetworkPortProfileAsync(profile);
            await db.AddAuditAsync(
                "VM_FIVEM_INTERNAL_PORT_REPAIRED",
                "vm",
                vmName,
                $"Corrected stale FiveM internal port {rawProfile.FiveMInternalPort} to {profile.FiveMInternalPort} before applying the network profile.");
        }

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
        var rawProfile = await db.GetVmNetworkPortProfileAsync(vmName);
        var profile = NormalizeNetworkProfile(rawProfile);
        if (rawProfile.FiveMInternalPort != profile.FiveMInternalPort)
        {
            await db.SaveVmNetworkPortProfileAsync(profile);
            await db.AddAuditAsync(
                "VM_FIVEM_INTERNAL_PORT_REPAIRED",
                "vm",
                vmName,
                $"Corrected stale FiveM internal port {rawProfile.FiveMInternalPort} to {profile.FiveMInternalPort} before syncing the guest firewall.");
        }

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
            var fiveMInternalPort = request.EnableFiveM
                ? ResolveProvisioningFiveMInternalPort(request.FiveMInternalPort, fiveMPort, defaults)
                : defaults.DefaultFiveMInternalPort;
            if (request.ConfigureWindowsGuest && request.EnableFiveM && fiveMInternalPort == 3389)
            {
                throw new InvalidOperationException("FiveM VM TCP port 3389 cannot be the same as the Windows guest RDP port. Choose a different FiveM VM port.");
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
                        await db.AddAuditAsync("VM_GUEST_RDP_PORT_SYNCED", "vm", vmName, $"Windows RDP listener changed to TCP + UDP {rdpStatus.Port} during provisioning.");
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

                    if (AgentlessRecoveryEnabled)
                    {
                        try
                        {
                            var recoveryUsername = RecoveryAccountUsername;
                            if (string.Equals(
                                    NormalizeLocalWindowsUsername(guestUsername),
                                    NormalizeLocalWindowsUsername(recoveryUsername),
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidOperationException("RecoveryAccountUsername cannot match the customer Windows username.");
                            }

                            var recoveryPassword = GenerateRecoveryPassword();
                            await powerShell.EnsureWindowsRecoveryAccountAsync(
                                vmName,
                                guestUsername,
                                request.TemplatePassword,
                                recoveryUsername,
                                recoveryPassword);

                            var recoveryNow = DateTime.UtcNow;
                            await db.SaveVmRecoveryAccountAsync(new VmRecoveryAccountSecret(
                                vmName,
                                true,
                                recoveryUsername,
                                recoveryPassword,
                                recoveryNow,
                                null,
                                string.Empty,
                                recoveryNow));

                            await db.AddAuditAsync(
                                "VM_AGENTLESS_RECOVERY_PROVISIONED",
                                "vm",
                                vmName,
                                $"Agentless recovery account {recoveryUsername} created and verified through PowerShell Direct. Secret stored encrypted.");
                        }
                        catch (Exception recoveryEx)
                        {
                            warnings.Add("VPS was created, but agentless emergency password recovery could not be initialized: " + recoveryEx.Message);
                            await db.SetVmRecoveryAccountErrorAsync(vmName, RecoveryAccountUsername, recoveryEx.Message);
                            await db.AddAuditAsync("VM_AGENTLESS_RECOVERY_PROVISION_WARNING", "vm", vmName, recoveryEx.Message);
                        }
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

                NetworkProfileApplyResult networkResult;
                try
                {
                    networkResult = await powerShell.ApplyNetworkPortProfileAsync(profile);
                }
                catch (Exception firstApplyException)
                {
                    // WHMCS provisioning must not quietly miss the Hyper-V host firewall.
                    // ApplyNetworkPortProfileAsync now verifies every host firewall rule;
                    // give transient Windows Firewall/PowerShell state one clean retry.
                    await db.AddAuditAsync(
                        "VM_NETWORK_PROFILE_RETRY",
                        "vm",
                        vmName,
                        $"Initial automatic host NAT/firewall apply failed during provisioning and will be retried: {firstApplyException.Message}");

                    await Task.Delay(1000);
                    networkResult = await powerShell.ApplyNetworkPortProfileAsync(profile);
                }

                await db.AddAuditAsync("VM_NETWORK_PROFILE_APPLIED", "vm", vmName, networkResult.Message);
            }
            catch (Exception ex)
            {
                warnings.Add("VM was created, but automatic NAT/RDP/FiveM mapping failed: " + ex.Message);
                await db.AddAuditAsync("VM_PROVISION_NETWORK_WARNING", "vm", vmName, ex.Message);
            }

            var customerRdpHost = GetRdpCustomerHost(defaults.PublicAddress);

            var access = new VmAccessInfo(
                vmName,
                customerRdpHost,
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
            // RDP may use a friendly customer-facing hostname, but FiveM should
            // always display the real public IPv4 address used by WinNAT.
            var rdpEndpoint = $"{customerRdpHost}:{rdpPort}";
            var fiveMEndpoint = request.EnableFiveM && fiveMPort is > 0
                ? $"{defaults.PublicAddress}:{fiveMPort}"
                : null;

            var emailBody = $"Hello {target.Username},\n\nYour virtual server is ready.\n\nVM: {vmName}\nTemplate: {template.Name}\nRDP: {rdpEndpoint}\nWindows RDP listener: TCP + UDP {profile.RdpInternalPort}\nWindows username: {guestUsername}";
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
                new("Windows RDP listener", $"TCP + UDP {profile.RdpInternalPort}"),
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

        var templates = await db.GetVmTemplatesAsync(
            enabledOnly || !currentUser.IsAdmin);

        if (!currentUser.IsAdmin && RequireValidatedTemplatesForClients)
        {
            var statuses = await GetCurrentVmTemplateValidationStatusesAsync();
            var validatedIds = statuses
                .Where(item =>
                    item.IsCurrent
                    && string.Equals(
                        item.Status,
                        "validated",
                        StringComparison.OrdinalIgnoreCase))
                .Select(item => item.TemplateId)
                .ToHashSet();

            return templates
                .Where(item => validatedIds.Contains(item.Id))
                .ToList();
        }

        return templates;
    }

    public async Task<IReadOnlyList<VmTemplateValidationStatus>> GetVmTemplateValidationStatusesAsync(
        PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await GetCurrentVmTemplateValidationStatusesAsync();
    }

    public async Task SaveVmTemplateGenerationAsync(
        PanelUser? currentUser,
        long templateId,
        int generation)
    {
        RequireAdmin(currentUser);
        generation = Math.Clamp(generation, 1, 2);

        var statuses = await db.GetVmTemplateValidationStatusesAsync();
        var previous = statuses.FirstOrDefault(item => item.TemplateId == templateId);
        if (previous is null)
        {
            throw new KeyNotFoundException("Template was not found.");
        }

        await db.SaveVmTemplateGenerationAsync(templateId, generation);

        if (previous.VmGeneration != generation)
        {
            await db.InvalidateVmTemplateValidationAsync(
                templateId,
                $"Hyper-V generation changed from {previous.VmGeneration} to {generation}; validate again.");
        }
    }

    public async Task<VmTemplateValidationStatus> ValidateVmTemplateAsync(
        PanelUser? currentUser,
        long templateId,
        Func<int, string, Task>? progress = null)
    {
        RequireAdmin(currentUser);

        var template = (await db.GetVmTemplatesAsync(false))
            .FirstOrDefault(item => item.Id == templateId)
            ?? throw new KeyNotFoundException("Template was not found.");

        var validationStates = await db.GetVmTemplateValidationStatusesAsync();
        var previousState = validationStates
            .FirstOrDefault(item => item.TemplateId == templateId);
        var generation = Math.Clamp(previousState?.VmGeneration ?? 2, 1, 2);

        var settings = ReadSmartReinstallSettings();
        var bootstrap = await ResolveReinstallBootstrapCredentialAsync(
            template,
            settings);

        if (string.IsNullOrWhiteSpace(bootstrap.Password))
        {
            var missing = NewTemplateValidationStatus(
                templateId,
                generation,
                "failed",
                "Validation cannot start because this template has no bootstrap Windows password.",
                checkedAt: DateTime.UtcNow);

            await db.SaveVmTemplateValidationStatusAsync(missing);
            return missing;
        }

        if (!string.Equals(
                template.OperatingSystem,
                "Windows",
                StringComparison.OrdinalIgnoreCase)
            && !template.OperatingSystem.Contains(
                "Windows",
                StringComparison.OrdinalIgnoreCase))
        {
            var unsupported = NewTemplateValidationStatus(
                templateId,
                generation,
                "failed",
                "Real boot validation currently supports Windows templates only.",
                checkedAt: DateTime.UtcNow);

            await db.SaveVmTemplateValidationStatusAsync(unsupported);
            return unsupported;
        }

        var validationRoot =
            (configuration["TemplateValidation:WorkingRoot"]
                ?? @"C:\ProgramData\PenguinPanel\TemplateValidation").Trim();
        var memoryMb = Math.Clamp(
            configuration.GetValue("TemplateValidation:MemoryMb", 2048),
            1024,
            8192);
        var processorCount = Math.Clamp(
            configuration.GetValue("TemplateValidation:ProcessorCount", 2),
            1,
            4);
        var bootTimeout = Math.Clamp(
            configuration.GetValue("TemplateValidation:BootTimeoutSeconds", 180),
            60,
            600);
        var enableSecureBoot =
            configuration.GetValue(
                "TemplateValidation:EnableSecureBootForGeneration2",
                true);

        await _templateValidationLock.WaitAsync();

        VmTemplateValidationSandbox? sandbox = null;
        var powerShellDirectOk = false;
        var rdpServiceFound = false;
        var rdpEnabled = false;
        var rdpStatus = string.Empty;
        var windowsProduct = string.Empty;
        var windowsVersion = string.Empty;
        var buildNumber = string.Empty;
        var architecture = string.Empty;
        long virtualDiskBytes = 0;
        long physicalDiskBytes = 0;
        long lastWriteTicks = 0;

        try
        {
            await ReportTemplateValidationProgressAsync(
                progress,
                5,
                "Checking template file and preparing an isolated validation VM...");

            await db.SaveVmTemplateValidationStatusAsync(
                NewTemplateValidationStatus(
                    templateId,
                    generation,
                    "validating",
                    "Validation is running."));

            sandbox = await powerShell.CreateVmTemplateValidationSandboxAsync(
                template.IsoPath,
                template.Name,
                generation,
                validationRoot,
                memoryMb,
                processorCount,
                enableSecureBoot);

            virtualDiskBytes = sandbox.VirtualDiskBytes;
            physicalDiskBytes = sandbox.PhysicalDiskBytes;
            lastWriteTicks = sandbox.TemplateLastWriteUtcTicks;

            await ReportTemplateValidationProgressAsync(
                progress,
                38,
                "Temporary validation VM started with networking removed...");

            await ReportTemplateValidationProgressAsync(
                progress,
                50,
                "Waiting for Windows and verifying the saved bootstrap login...");

            await powerShell.WaitForPowerShellDirectAsync(
                sandbox.VmName,
                bootstrap.Username,
                bootstrap.Password,
                bootTimeout);
            powerShellDirectOk = true;

            await ReportTemplateValidationProgressAsync(
                progress,
                72,
                "Reading Windows version, architecture and Remote Desktop service state...");

            var guest = await powerShell.GetVmTemplateGuestValidationInfoAsync(
                sandbox.VmName,
                bootstrap.Username,
                bootstrap.Password);

            windowsProduct = guest.WindowsProductName;
            windowsVersion = guest.WindowsVersion;
            buildNumber = guest.BuildNumber;
            architecture = guest.Architecture;
            rdpServiceFound = guest.RdpServiceFound;
            rdpEnabled = guest.RdpEnabled;
            rdpStatus = guest.RdpServiceStatus;

            if (!guest.RdpServiceFound)
            {
                throw new InvalidOperationException(
                    "Windows booted and the bootstrap login worked, but the Remote Desktop Services service (TermService) was not found.");
            }

            await ReportTemplateValidationProgressAsync(
                progress,
                90,
                "Validation checks passed. Removing the temporary test VM...");

            await powerShell.CleanupVmTemplateValidationSandboxAsync(
                sandbox.VmName,
                sandbox.WorkingDirectory,
                validationRoot);
            sandbox = null;

            var rdpDescription = rdpEnabled
                ? $"RDP is enabled; TermService={rdpStatus}."
                : $"RDP service exists; current template RDP is disabled and Smart Reinstall will enable/configure it.";

            var success = new VmTemplateValidationStatus(
                templateId,
                generation,
                "validated",
                $"Validated successfully. {rdpDescription}",
                windowsProduct,
                windowsVersion,
                buildNumber,
                architecture,
                true,
                rdpServiceFound,
                rdpEnabled,
                rdpStatus,
                virtualDiskBytes,
                physicalDiskBytes,
                lastWriteTicks,
                DateTime.UtcNow,
                true);

            await db.SaveVmTemplateValidationStatusAsync(success);

            await db.AddAuditAsync(
                "VM_TEMPLATE_VALIDATED",
                "template",
                templateId.ToString(),
                $"{template.Name}; generation={generation}; Windows={windowsProduct}; version={windowsVersion}; build={buildNumber}; architecture={architecture}; PowerShellDirect=OK; RDP service={rdpStatus}; temporary VM cleaned.");

            await ReportTemplateValidationProgressAsync(
                progress,
                100,
                "Template validated and ready for client reinstall.");

            return success;
        }
        catch (Exception ex)
        {
            var cleanupError = string.Empty;

            if (sandbox is not null)
            {
                try
                {
                    await powerShell.CleanupVmTemplateValidationSandboxAsync(
                        sandbox.VmName,
                        sandbox.WorkingDirectory,
                        validationRoot);
                }
                catch (Exception cleanupEx)
                {
                    cleanupError =
                        " Temporary validation VM cleanup also failed: "
                        + cleanupEx.Message;
                }
            }

            var failure = new VmTemplateValidationStatus(
                templateId,
                generation,
                "failed",
                ex.Message + cleanupError,
                windowsProduct,
                windowsVersion,
                buildNumber,
                architecture,
                powerShellDirectOk,
                rdpServiceFound,
                rdpEnabled,
                rdpStatus,
                virtualDiskBytes,
                physicalDiskBytes,
                lastWriteTicks,
                DateTime.UtcNow,
                false);

            await db.SaveVmTemplateValidationStatusAsync(failure);

            await db.AddAuditAsync(
                "VM_TEMPLATE_VALIDATION_FAILED",
                "template",
                templateId.ToString(),
                $"{template.Name}; generation={generation}; error={ex.Message}; cleanupError={(string.IsNullOrWhiteSpace(cleanupError) ? "none" : cleanupError)}. Bootstrap password value was not logged.");

            await ReportTemplateValidationProgressAsync(
                progress,
                100,
                "Template validation failed.");

            return failure;
        }
        finally
        {
            _templateValidationLock.Release();
        }
    }

    private static Task ReportTemplateValidationProgressAsync(
        Func<int, string, Task>? progress,
        int percent,
        string status) =>
        progress is null
            ? Task.CompletedTask
            : progress(Math.Clamp(percent, 0, 100), status);

    private async Task<IReadOnlyList<VmTemplateValidationStatus>> GetCurrentVmTemplateValidationStatusesAsync()
    {
        var templates = await db.GetVmTemplatesAsync(false);
        var statuses = await db.GetVmTemplateValidationStatusesAsync();
        var templateMap = templates.ToDictionary(item => item.Id);

        return statuses
            .Select(status =>
            {
                if (!templateMap.TryGetValue(status.TemplateId, out var template))
                {
                    return status with { IsCurrent = false };
                }

                return status with
                {
                    IsCurrent = IsTemplateValidationCurrent(
                        template,
                        status)
                };
            })
            .ToList();
    }

    private static bool IsTemplateValidationCurrent(
        VmTemplate template,
        VmTemplateValidationStatus status)
    {
        if (!string.Equals(
                status.Status,
                "validated",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(template.IsoPath))
            {
                return false;
            }

            var file = new FileInfo(template.IsoPath);
            return file.Exists
                && file.Length == status.PhysicalDiskBytes
                && file.LastWriteTimeUtc.Ticks
                    == status.TemplateLastWriteUtcTicks;
        }
        catch
        {
            return false;
        }
    }

    private static VmTemplateValidationStatus NewTemplateValidationStatus(
        long templateId,
        int generation,
        string status,
        string message,
        DateTime? checkedAt = null) =>
        new(
            templateId,
            Math.Clamp(generation, 1, 2),
            status,
            message,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            false,
            false,
            string.Empty,
            0,
            0,
            0,
            checkedAt,
            false);

    private async Task EnsureTemplateValidatedForClientUseAsync(
        VmTemplate template)
    {
        if (!RequireValidatedTemplatesForClients)
        {
            return;
        }

        var statuses = await GetCurrentVmTemplateValidationStatusesAsync();
        var status = statuses.FirstOrDefault(
            item => item.TemplateId == template.Id);

        if (status is null
            || !status.IsCurrent
            || !string.Equals(
                status.Status,
                "validated",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This operating-system template is not currently validated. Please choose another template or contact support.");
        }
    }

    public async Task<IReadOnlyList<VmTemplateBootstrapStatus>> GetVmTemplateBootstrapStatusesAsync(
        PanelUser? currentUser)
    {
        RequireAdmin(currentUser);
        return await db.GetVmTemplateBootstrapStatusesAsync();
    }

    public async Task SaveVmTemplateBootstrapCredentialAsync(
        PanelUser? currentUser,
        long templateId,
        string username,
        string password)
    {
        RequireAdmin(currentUser);

        var templateExists = (await db.GetVmTemplatesAsync(false))
            .Any(item => item.Id == templateId);
        if (!templateExists)
        {
            throw new KeyNotFoundException("Template was not found.");
        }

        username = string.IsNullOrWhiteSpace(username)
            ? "Administrator"
            : username.Trim();

        var before = await db.GetVmTemplateBootstrapCredentialAsync(templateId);
        var suppliedPassword = password ?? string.Empty;
        var credentialChanged =
            !string.Equals(
                before.Username,
                username,
                StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(suppliedPassword)
                && !string.Equals(
                    before.Password,
                    suppliedPassword,
                    StringComparison.Ordinal));

        await db.SaveVmTemplateBootstrapCredentialAsync(
            templateId,
            username,
            suppliedPassword);

        if (credentialChanged)
        {
            await db.InvalidateVmTemplateValidationAsync(
                templateId,
                "Bootstrap Windows credential changed; validate again.");
        }

        await db.AddAuditAsync(
            "VM_TEMPLATE_BOOTSTRAP_CREDENTIAL_SAVED",
            "template",
            templateId.ToString(),
            string.IsNullOrWhiteSpace(password)
                ? $"Bootstrap username updated to {username}; existing encrypted password kept."
                : $"Bootstrap credential updated for {username}; password stored encrypted.");
    }

    public async Task<long> SaveVmTemplateAsync(PanelUser? currentUser, VmTemplate template)
    {
        RequireAdmin(currentUser);
        if (string.IsNullOrWhiteSpace(template.Name))
        {
            throw new InvalidOperationException("Template name is required.");
        }

        var existing = template.Id > 0
            ? (await db.GetVmTemplatesAsync(false))
                .FirstOrDefault(item => item.Id == template.Id)
            : (await db.GetVmTemplatesAsync(false))
                .FirstOrDefault(item =>
                    string.Equals(
                        item.Name,
                        template.Name.Trim(),
                        StringComparison.OrdinalIgnoreCase));

        var id = await db.SaveVmTemplateAsync(
            template with { Name = template.Name.Trim() });

        if (existing is not null
            && !string.Equals(
                existing.IsoPath,
                template.IsoPath,
                StringComparison.OrdinalIgnoreCase))
        {
            await db.InvalidateVmTemplateValidationAsync(
                id,
                "Template VHD/VHDX path changed; validate again.");
        }

        await db.AddAuditAsync(
            "VM_TEMPLATE_SAVED",
            "template",
            id.ToString(),
            $"{template.Name} by {currentUser!.Username}");
        return id;
    }

    public async Task<VmTemplateCreateResult> CreateTemplateFromVmAsync(
        PanelUser? currentUser,
        string sourceVmName,
        string templateName,
        string operatingSystem,
        string destinationFolder,
        string notes,
        bool enabled,
        string bootstrapUsername,
        string bootstrapPassword)
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

        var result = await powerShell.CreateTemplateFromVmDiskAsync(
            sourceVmName,
            destinationFolder,
            templateName);

        var template = new VmTemplate(
            0,
            templateName,
            operatingSystem,
            result.TemplatePath,
            notes ?? string.Empty,
            enabled,
            DateTime.UtcNow);

        var id = await db.SaveVmTemplateAsync(template);

        var effectiveBootstrapUsername =
            string.IsNullOrWhiteSpace(bootstrapUsername)
                ? "Administrator"
                : bootstrapUsername.Trim();
        var effectiveBootstrapPassword = bootstrapPassword ?? string.Empty;
        var bootstrapSource = "admin entered";

        if (string.IsNullOrWhiteSpace(effectiveBootstrapPassword))
        {
            var sourceAccess = await db.GetVmAccessInfoAsync(sourceVmName);

            if (!string.IsNullOrWhiteSpace(sourceAccess.Password))
            {
                effectiveBootstrapPassword = sourceAccess.Password;
                effectiveBootstrapUsername =
                    string.IsNullOrWhiteSpace(sourceAccess.Username)
                        ? "Administrator"
                        : sourceAccess.Username.Trim();
                bootstrapSource = "inherited from source VM encrypted Access credential";
            }
            else
            {
                bootstrapSource = "not available";
            }
        }

        await db.SaveVmTemplateBootstrapCredentialAsync(
            id,
            effectiveBootstrapUsername,
            effectiveBootstrapPassword);
        await db.SaveVmTemplateGenerationAsync(
            id,
            result.Generation);

        await db.AddAuditAsync(
            "VM_TEMPLATE_CREATED_FROM_VM",
            "template",
            id.ToString(),
            $"{templateName} from {sourceVmName} by {currentUser!.Username}. "
            + $"Path: {result.TemplatePath}; bootstrap username={effectiveBootstrapUsername}; "
            + $"bootstrap source={bootstrapSource}; "
            + $"credential={(string.IsNullOrWhiteSpace(effectiveBootstrapPassword) ? "not set" : "encrypted")}. "
            + "Password value was not logged.");

        return result;
    }

    public async Task<VmReinstallPreflight> GetReinstallPreflightAsync(
        PanelUser? currentUser,
        string vmName,
        long templateId)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(
            currentUser,
            currentUser!.EffectivePermissions.CanRequestReinstall,
            "Self-service reinstall is not enabled for your account.");

        var enabledOnly = !currentUser.IsAdmin;
        var template = (await db.GetVmTemplatesAsync(enabledOnly))
            .FirstOrDefault(item => item.Id == templateId)
            ?? throw new InvalidOperationException(
                enabledOnly
                    ? "Template was not found or is not enabled for clients."
                    : "Template was not found.");

        if (!currentUser.IsAdmin)
        {
            await EnsureTemplateValidatedForClientUseAsync(template);
        }

        return await BuildReinstallPreflightAsync(vmName, template);
    }

    public async Task<VmReinstallResult> RunReinstallRequestAsync(
        PanelUser? currentUser,
        long requestId,
        bool startAfter = true)
    {
        RequireAdmin(currentUser);

        var job = await db.GetReinstallJobAsync(requestId)
            ?? throw new InvalidOperationException("Reinstall request was not found.");

        var template = (await db.GetVmTemplatesAsync(false))
            .FirstOrDefault(item => item.Id == job.TemplateId)
            ?? throw new InvalidOperationException("Reinstall template no longer exists.");

        await EnsureTemplateValidatedForClientUseAsync(template);
        await db.UpdateReinstallRequestStatusAsync(requestId, "approved");

        try
        {
            var result = await RunSmartReinstallCoreAsync(
                currentUser!,
                job.VmName,
                template,
                startAfter,
                $"Request #{requestId}",
                requestedWindowsPassword: string.Empty,
                progress: null);

            await db.UpdateReinstallRequestStatusAsync(requestId, "completed");
            return result;
        }
        catch
        {
            await db.UpdateReinstallRequestStatusAsync(requestId, "failed");
            throw;
        }
    }

    public async Task<VmReinstallResult> RunDirectReinstallAsync(
        PanelUser? currentUser,
        string vmName,
        long templateId,
        bool startAfter = true,
        string notes = "",
        string requestedWindowsPassword = "",
        Func<int, string, Task>? progress = null)
    {
        RequireUserPermission(
            currentUser,
            currentUser?.EffectivePermissions.CanRequestReinstall == true,
            "Self-service reinstall is not enabled for your account.");
        await EnsureCanAccessVmAsync(currentUser, vmName);

        var enabledOnly = currentUser is not null && !currentUser.IsAdmin;
        var template = (await db.GetVmTemplatesAsync(enabledOnly))
            .FirstOrDefault(item => item.Id == templateId)
            ?? throw new InvalidOperationException(
                enabledOnly
                    ? "Template was not found or is not enabled for clients."
                    : "Template was not found.");

        if (!currentUser!.IsAdmin)
        {
            await EnsureTemplateValidatedForClientUseAsync(template);
        }

        if (!string.IsNullOrWhiteSpace(requestedWindowsPassword))
        {
            ValidatePassword(requestedWindowsPassword);
        }

        return await RunSmartReinstallCoreAsync(
            currentUser!,
            vmName,
            template,
            startAfter,
            $"Direct reinstall. Notes: {notes}",
            requestedWindowsPassword,
            progress);
    }

    private static Task ReportReinstallProgressAsync(
        Func<int, string, Task>? progress,
        int percent,
        string status) =>
        progress is null
            ? Task.CompletedTask
            : progress(Math.Clamp(percent, 0, 100), status);

    private async Task<VmReinstallResult> RunSmartReinstallCoreAsync(
        PanelUser currentUser,
        string vmName,
        VmTemplate template,
        bool startAfter,
        string auditContext,
        string requestedWindowsPassword,
        Func<int, string, Task>? progress)
    {
        var extension = Path.GetExtension(template.IsoPath);
        if (!string.Equals(extension, ".vhd", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".vhdx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Automatic reinstall only supports VHD/VHDX templates.");
        }

        await _provisioningLock.WaitAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(requestedWindowsPassword))
            {
                ValidatePassword(requestedWindowsPassword);
            }

            await CleanupExpiredReinstallRollbackCoreAsync(vmName);
            var protectedRollback =
                await db.GetAvailableVmReinstallRollbackAsync(vmName);
            if (protectedRollback is not null)
            {
                throw new InvalidOperationException(
                    $"A previous Windows installation is still protected until {protectedRollback.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm}. "
                    + "Restore it, let it expire, or ask an administrator to delete it before reinstalling this VPS again.");
            }

            var preflight = await BuildReinstallPreflightAsync(vmName, template);
            if (!preflight.CanProceed)
            {
                throw new InvalidOperationException(
                    preflight.Summary + " " + string.Join(" ", preflight.Warnings));
            }

            await db.AddAuditAsync(
                "VM_REINSTALL_STARTED",
                "vm",
                vmName,
                $"{auditContext}; template {template.Name}; VPS disk {FormatBytesAsGb(preflight.CurrentVirtualDiskBytes)} GB; template {FormatBytesAsGb(preflight.TemplateVirtualDiskBytes)} GB; by {currentUser.Username}.");

            await ReportReinstallProgressAsync(progress, 18, "Loading the saved VPS network and access profile...");
            var profile = NormalizeNetworkProfile(
                await db.GetVmNetworkPortProfileAsync(vmName));
            var previousAccess = await db.GetVmAccessInfoAsync(vmName);
            var settings = ReadSmartReinstallSettings();
            var bootstrap = await ResolveReinstallBootstrapCredentialAsync(
                template,
                settings);
            var warnings = new List<string>(preflight.Warnings);

            var canConfigureGuest =
                settings.ConfigureWindowsGuest
                && !string.IsNullOrWhiteSpace(bootstrap.Password);

            if (settings.ConfigureWindowsGuest && !canConfigureGuest)
            {
                throw new InvalidOperationException(
                    $"Smart reinstall cannot start because template '{template.Name}' has no bootstrap Windows credential. "
                    + "Open Admin -> OS Templates and save the Windows username/password that is actually baked into this golden VHDX. "
                    + "This is a private template credential, not the client's final reinstall password.");
            }

            // Host WinNAT and host Windows Firewall do not depend on guest credentials.
            // Verify/rebuild them BEFORE destructive disk replacement so a broken host
            // network profile cannot leave a freshly reinstalled customer VM unreachable.
            if (profile.HasRdpRule
                || profile.HasFiveMRule
                || !string.IsNullOrWhiteSpace(profile.ExtraTcpPorts)
                || !string.IsNullOrWhiteSpace(profile.ExtraUdpPorts))
            {
                await ReportReinstallProgressAsync(progress, 23, "Verifying Hyper-V host WinNAT and Windows Firewall...");
                var hostPreflightApply = await powerShell.ApplyNetworkPortProfileAsync(profile);
                await db.AddAuditAsync(
                    "VM_REINSTALL_HOST_NETWORK_PREFLIGHT_READY",
                    "vm",
                    vmName,
                    hostPreflightApply.Message);
            }

            var effectiveStartAfter = startAfter || canConfigureGuest;

            await ReportReinstallProgressAsync(progress, 32, $"Copying and attaching the {template.Name} OS disk...");
            await availability.MarkPlannedDowntimeAsync(vmName, $"Smart Reinstall by {currentUser.Username}", TimeSpan.FromHours(4));
            var result = await powerShell.ReinstallVmFromTemplateDiskAsync(
                vmName,
                template.Name,
                template.IsoPath,
                effectiveStartAfter);
            await ReportReinstallProgressAsync(progress, 48, "Fresh Windows disk attached and VM started. Waiting for Windows...");

            var guestConfigured = false;
            var windowsUsername = string.Empty;
            var finalWindowsPassword = string.Empty;
            var finalRdpHost = string.Empty;
            int? finalRdpPort = null;

            if (canConfigureGuest)
            {
                windowsUsername = bootstrap.Username;

                try
                {
                    await ReportReinstallProgressAsync(progress, 52, "Waiting for Windows and verifying the template bootstrap login...");
                    await powerShell.WaitForPowerShellDirectAsync(
                        vmName,
                        bootstrap.Username,
                        bootstrap.Password,
                        settings.PowerShellDirectTimeoutSeconds);

                    await ReportReinstallProgressAsync(progress, 60, "Applying the saved internal IPv4, gateway and DNS settings...");
                    var guestNetwork = await powerShell.RepairWindowsGuestNetworkAsync(
                        profile,
                        bootstrap.Username,
                        bootstrap.Password,
                        PanelPublicUrl);

                    await db.AddAuditAsync(
                        "VM_REINSTALL_GUEST_NETWORK_CONFIGURED",
                        "vm",
                        vmName,
                        guestNetwork.Message);

                    if (profile.EnableRdp)
                    {
                        var desiredRdpPort =
                            profile.RdpExternalPort is > 0
                                ? profile.RdpExternalPort.Value
                                : profile.RdpInternalPort > 0
                                    ? profile.RdpInternalPort
                                    : 3389;

                        var rdp = await powerShell.SetWindowsGuestRdpPortAsync(
                            vmName,
                            bootstrap.Username,
                            bootstrap.Password,
                            desiredRdpPort);

                        profile = profile with
                        {
                            RdpInternalPort = rdp.Port,
                            UpdatedAt = DateTime.UtcNow
                        };
                        await db.SaveVmNetworkPortProfileAsync(profile);
                    }

                    await ReportReinstallProgressAsync(progress, 74, "Restoring Windows guest firewall rules...");
                    var firewall = await powerShell.SyncWindowsGuestFirewallRulesAsync(
                        profile,
                        bootstrap.Username,
                        bootstrap.Password);

                    await db.AddAuditAsync(
                        "VM_REINSTALL_GUEST_FIREWALL_SYNCED",
                        "vm",
                        vmName,
                        firewall.Message);

                    try
                    {
                        await ReportReinstallProgressAsync(progress, 79, "Expanding the Windows C: partition to the purchased disk size...");
                        var expanded = await powerShell.ExpandWindowsGuestSystemPartitionAsync(
                            vmName,
                            bootstrap.Username,
                            bootstrap.Password);

                        await db.AddAuditAsync(
                            "VM_REINSTALL_GUEST_DISK_EXPAND",
                            "vm",
                            vmName,
                            expanded
                                ? "Windows C: expanded to the purchased VPS disk size."
                                : "Windows C: already used available disk space.");
                    }
                    catch (Exception expandEx)
                    {
                        warnings.Add("Windows was reinstalled, but C: could not be expanded automatically: " + expandEx.Message);
                    }

                    if (AgentlessRecoveryEnabled)
                    {
                        try
                        {
                            if (string.Equals(
                                    NormalizeLocalWindowsUsername(bootstrap.Username),
                                    NormalizeLocalWindowsUsername(RecoveryAccountUsername),
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidOperationException(
                                    "RecoveryAccountUsername cannot match the template Windows username.");
                            }

                            var recoveryPassword = GenerateRecoveryPassword();
                            await powerShell.EnsureWindowsRecoveryAccountAsync(
                                vmName,
                                bootstrap.Username,
                                bootstrap.Password,
                                RecoveryAccountUsername,
                                recoveryPassword);

                            var now = DateTime.UtcNow;
                            await db.SaveVmRecoveryAccountAsync(new VmRecoveryAccountSecret(
                                vmName,
                                true,
                                RecoveryAccountUsername,
                                recoveryPassword,
                                now,
                                null,
                                string.Empty,
                                now));

                            await db.AddAuditAsync(
                                "VM_REINSTALL_AGENTLESS_RECOVERY_READY",
                                "vm",
                                vmName,
                                $"Agentless recovery account {RecoveryAccountUsername} recreated.");
                        }
                        catch (Exception recoveryEx)
                        {
                            warnings.Add(
                                "Agentless recovery could not be initialized after reinstall: "
                                + recoveryEx.Message);
                            await db.SetVmRecoveryAccountErrorAsync(
                                vmName,
                                RecoveryAccountUsername,
                                recoveryEx.Message);
                        }
                    }

                    // A blank self-service password always means generate a fresh
                    // customer credential. Never expose/retain the hidden golden-image
                    // bootstrap password as the customer's final Windows login.
                    var customerPassword =
                        !string.IsNullOrWhiteSpace(requestedWindowsPassword)
                            ? requestedWindowsPassword
                            : GenerateFriendlyPassword();

                    await ReportReinstallProgressAsync(progress, 88, "Setting the final Windows / Remote Desktop password...");
                    if (!string.Equals(
                            customerPassword,
                            bootstrap.Password,
                            StringComparison.Ordinal))
                    {
                        await powerShell.ChangeWindowsGuestPasswordAsync(
                            vmName,
                            bootstrap.Username,
                            bootstrap.Password,
                            customerPassword);
                    }

                    await ReportReinstallProgressAsync(progress, 92, "Verifying the final Windows login...");
                    await powerShell.WaitForPowerShellDirectAsync(
                        vmName,
                        bootstrap.Username,
                        customerPassword,
                        Math.Min(settings.PowerShellDirectTimeoutSeconds, 120));

                    if (profile.EnableRdp)
                    {
                        _ = await powerShell.GetWindowsGuestRdpListenerStatusAsync(
                            vmName,
                            bootstrap.Username,
                            customerPassword);
                    }

                    var publicIp =
                        string.IsNullOrWhiteSpace(profile.PublicIpAddress)
                            ? previousAccess.IpAddress
                            : profile.PublicIpAddress.Trim();
                    var profileOrExistingHost =
                        string.IsNullOrWhiteSpace(profile.PublicHostname)
                            ? previousAccess.Hostname
                            : profile.PublicHostname.Trim();
                    var publicHost = GetRdpCustomerHost(
                        publicIp,
                        profileOrExistingHost);

                    await db.SaveVmAccessInfoAsync(new VmAccessInfo(
                        vmName,
                        publicHost,
                        publicIp,
                        profile.RdpExternalPort,
                        previousAccess.SshPort,
                        bootstrap.Username,
                        customerPassword,
                        previousAccess.Notes,
                        previousAccess.WebConsoleUrl,
                        DateTime.UtcNow));

                    finalWindowsPassword = customerPassword;
                    finalRdpHost = !string.IsNullOrWhiteSpace(publicHost)
                        ? publicHost
                        : publicIp;
                    finalRdpPort = profile.RdpExternalPort;
                    guestConfigured = true;
                    await ReportReinstallProgressAsync(progress, 95, "Windows guest configuration verified successfully...");

                    await db.AddAuditAsync(
                        "VM_REINSTALL_GUEST_READY",
                        "vm",
                        vmName,
                        $"Windows guest configuration completed for {bootstrap.Username}; final password source={(string.IsNullOrWhiteSpace(requestedWindowsPassword) ? "PenguinPanel generated" : "customer selected")}. Password value was not logged.");
                }
                catch (Exception guestEx)
                {
                    await ReportReinstallProgressAsync(
                        progress,
                        94,
                        "Windows setup failed. Restoring the previous OS disk automatically...");

                    await db.AddAuditAsync(
                        "VM_REINSTALL_GUEST_FAILED",
                        "vm",
                        vmName,
                        guestEx.Message);

                    try
                    {
                        var rollbackMessage = await powerShell.RollbackFailedReinstallAsync(
                            vmName,
                            result.NewDiskPath,
                            result.OldDiskRollbackPath,
                            startAfter: true);

                        await db.AddAuditAsync(
                            "VM_REINSTALL_AUTO_ROLLBACK_COMPLETED",
                            "vm",
                            vmName,
                            rollbackMessage);

                        throw new InvalidOperationException(
                            "Smart Reinstall could not finish the Windows setup, so PenguinPanel restored and started the previous OS automatically. "
                            + "The fresh template bootstrap/login or guest configuration needs attention. Cause: "
                            + guestEx.Message);
                    }
                    catch (Exception rollbackEx) when (rollbackEx is not InvalidOperationException
                        || !rollbackEx.Message.StartsWith("Smart Reinstall could not finish", StringComparison.Ordinal))
                    {
                        await db.AddAuditAsync(
                            "VM_REINSTALL_AUTO_ROLLBACK_FAILED",
                            "vm",
                            vmName,
                            rollbackEx.Message);

                        throw new InvalidOperationException(
                            "CRITICAL: the fresh Windows setup failed and PenguinPanel could not automatically restore the previous OS disk. "
                            + $"Fresh disk: {result.NewDiskPath}. Rollback disk: {result.OldDiskRollbackPath}. "
                            + $"Setup error: {guestEx.Message}. Rollback error: {rollbackEx.Message}");
                    }
                }
            }
            else if (!settings.ConfigureWindowsGuest)
            {
                warnings.Add(
                    "Automatic Windows guest setup is disabled by Reinstall:ConfigureWindowsGuest.");
            }

            try
            {
                if (profile.HasRdpRule
                    || profile.HasFiveMRule
                    || profile.HasGuestDnsSettings
                    || !string.IsNullOrWhiteSpace(profile.ExtraTcpPorts)
                    || !string.IsNullOrWhiteSpace(profile.ExtraUdpPorts))
                {
                    await ReportReinstallProgressAsync(progress, 97, "Final verification of host WinNAT and Windows Firewall mappings...");
                    var apply = await powerShell.ApplyNetworkPortProfileAsync(profile);
                    await UpdateAccessFromNetworkProfileAsync(profile);
                    await db.AddAuditAsync(
                        "VM_NETWORK_PROFILE_APPLIED_AFTER_REINSTALL",
                        "vm",
                        vmName,
                        "Verified host WinNAT + host Windows Firewall after reinstall. " + apply.Message);
                }
            }
            catch (Exception profileEx)
            {
                warnings.Add(
                    "Host WinNAT/firewall re-apply failed after reinstall: "
                    + profileEx.Message);
            }

            if (canConfigureGuest && !startAfter)
            {
                try
                {
                    await powerShell.StopVmForCheckpointRestoreAsync(vmName);
                }
                catch (Exception stopEx)
                {
                    warnings.Add(
                        "VM could not be returned to the requested stopped state: "
                        + stopEx.Message);
                }
            }

            await ReportReinstallProgressAsync(progress, 99, "Saving final access details and protecting the previous Windows installation...");
            var finalState = await powerShell.GetVmStateAsync(vmName);

            VmReinstallRollbackInfo? managedRollback = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(result.OldDiskRollbackPath)
                    && File.Exists(result.OldDiskRollbackPath))
                {
                    var rollbackFile = new FileInfo(
                        result.OldDiskRollbackPath);
                    var createdAt = DateTime.UtcNow;
                    var expiresAt = createdAt.AddHours(
                        ReinstallRollbackRetentionHours);

                    var newRollback = new VmReinstallRollbackInfo(
                        0,
                        vmName,
                        template.Name,
                        result.OldDiskRollbackPath,
                        result.NewDiskPath,
                        result.DiskSizeBytes,
                        rollbackFile.Length,
                        "available",
                        result.WasRunning,
                        result.PreviousState,
                        createdAt,
                        expiresAt,
                        null,
                        null,
                        string.Empty);

                    var rollbackId =
                        await db.CreateVmReinstallRollbackAsync(
                            newRollback,
                            previousAccess,
                            currentUser.Id,
                            currentUser.Username);

                    managedRollback = newRollback with
                    {
                        Id = rollbackId
                    };

                    await db.AddAuditAsync(
                        "VM_REINSTALL_ROLLBACK_PROTECTED",
                        "vm",
                        vmName,
                        $"Previous OS rollback #{rollbackId} protected until {expiresAt:o}; physical={rollbackFile.Length} bytes; virtual={result.DiskSizeBytes} bytes.");
                }
            }
            catch (Exception rollbackRecordEx)
            {
                warnings.Add(
                    "Reinstall succeeded, but PenguinPanel could not register the previous OS disk in Rollback Manager: "
                    + rollbackRecordEx.Message);

                await db.AddAuditAsync(
                    "VM_REINSTALL_ROLLBACK_REGISTER_FAILED",
                    "vm",
                    vmName,
                    rollbackRecordEx.Message);
            }

            var customerMessage =
                guestConfigured
                    ? $"Reinstall complete! {template.Name} is ready to use. "
                        + "Your Remote Desktop, network, Windows Firewall, disk and Windows login were configured automatically. "
                        + "Open Access to view your login details."
                        + (managedRollback is null
                            ? string.Empty
                            : $" Your previous Windows installation is protected until {managedRollback.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm} and can be restored from Reinstall.")
                    : $"Reinstall complete! {template.Name} was installed, but some Windows settings still need attention.";

            if (warnings.Count > 0)
            {
                customerMessage += $" Please review {warnings.Count} warning(s) in the reinstall status.";
            }

            var finalResult = result with
            {
                FinalState = finalState,
                GuestConfigured = guestConfigured,
                WindowsUsername = windowsUsername,
                Warnings = warnings,
                Message = customerMessage
            };

            await db.AddAuditAsync(
                "VM_REINSTALL_COMPLETED",
                "vm",
                vmName,
                $"{auditContext}; template {template.Name}; new disk {result.NewDiskPath}; rollback {result.OldDiskRollbackPath}; guestConfigured={guestConfigured}; warnings={warnings.Count}. Technical disk paths are retained in audit only and are not shown in the customer success message.");

            var templateNotesMessage = string.IsNullOrWhiteSpace(template.Notes)
                ? string.Empty
                : $"\n\nAfter reinstall:\n{template.Notes.Trim()}";

            if (guestConfigured
                && !string.IsNullOrWhiteSpace(finalWindowsPassword)
                && !string.IsNullOrWhiteSpace(windowsUsername))
            {
                var emailCount = await NotifyAssignedUsersReinstallCredentialsAsync(
                    vmName,
                    template.Name,
                    finalState,
                    windowsUsername,
                    finalWindowsPassword,
                    finalRdpHost,
                    finalRdpPort,
                    string.IsNullOrWhiteSpace(requestedWindowsPassword),
                    template.Notes);

                await db.AddAuditAsync(
                    "VM_REINSTALL_CREDENTIAL_EMAIL",
                    "vm",
                    vmName,
                    $"Reinstall credential email delivery completed for {emailCount} assigned account(s). Password value was not logged.");
            }
            else
            {
                await NotifyAssignedUsersAsync(
                    vmName,
                    $"PenguinPanel: {vmName} reinstall completed",
                    $"The virtual server {vmName} was reinstalled from template {template.Name}. Current state: {finalState}.\n\nSome Windows guest settings may need administrator review."
                    + templateNotesMessage);
            }

            return finalResult;
        }
        catch (Exception ex)
        {
            await db.AddAuditAsync(
                "VM_REINSTALL_FAILED",
                "vm",
                vmName,
                $"{auditContext}; template {template.Name}; error: {ex.Message}");
            throw;
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    private async Task<VmTemplateBootstrapCredential> ResolveReinstallBootstrapCredentialAsync(
        VmTemplate template,
        SmartReinstallSettings settings)
    {
        var perTemplate = await db.GetVmTemplateBootstrapCredentialAsync(template.Id);
        if (!string.IsNullOrWhiteSpace(perTemplate.Password))
        {
            return perTemplate;
        }

        if (!string.IsNullOrWhiteSpace(settings.TemplatePassword))
        {
            return new VmTemplateBootstrapCredential(
                template.Id,
                string.IsNullOrWhiteSpace(settings.TemplateUsername)
                    ? "Administrator"
                    : settings.TemplateUsername.Trim(),
                settings.TemplatePassword);
        }

        return new VmTemplateBootstrapCredential(
            template.Id,
            string.IsNullOrWhiteSpace(perTemplate.Username)
                ? "Administrator"
                : perTemplate.Username,
            string.Empty);
    }

    private async Task<VmReinstallPreflight> BuildReinstallPreflightAsync(
        string vmName,
        VmTemplate template)
    {
        var preflight = await powerShell.GetVmReinstallPreflightAsync(
            vmName,
            template.Name,
            template.IsoPath);

        var warnings = preflight.Warnings.ToList();
        var canProceed = preflight.CanProceed;

        var storage = new VmStorageRootStatus(
            Path.GetDirectoryName(preflight.CurrentDiskPath) ?? string.Empty,
            false,
            true,
            preflight.TargetFreeBytes,
            preflight.TargetTotalBytes,
            string.Empty);

        var reserve = CalculateStorageReserveBytes(
            storage,
            ReadMinimumFreeSpaceAfterProvisionGb(),
            ReadMinimumFreeSpaceAfterProvisionPercent());

        if (preflight.TargetFreeBytes
            < preflight.TemplatePhysicalDiskBytes + reserve)
        {
            canProceed = false;
            warnings.Add(
                "VM storage cannot safely fit the fresh template copy while preserving the configured reserve. "
                + $"Free={FormatBytesAsGb(preflight.TargetFreeBytes)} GB; "
                + $"template≈{FormatBytesAsGb(preflight.TemplatePhysicalDiskBytes)} GB; "
                + $"reserve={FormatBytesAsGb(reserve)} GB.");
        }

        var settings = ReadSmartReinstallSettings();
        var bootstrap = await ResolveReinstallBootstrapCredentialAsync(
            template,
            settings);
        var guestConfigured =
            settings.ConfigureWindowsGuest
            && !string.IsNullOrWhiteSpace(bootstrap.Password);

        if (settings.ConfigureWindowsGuest && !guestConfigured)
        {
            canProceed = false;
            warnings.Add(
                $"Smart Windows setup requires a bootstrap credential for template '{template.Name}'. "
                + "Open OS Templates as an administrator and save the template's Windows Administrator bootstrap password before reinstalling.");
        }

        return preflight with
        {
            RequiredReserveBytes = reserve,
            GuestAutomationConfigured = guestConfigured,
            CanProceed = canProceed,
            Warnings = warnings,
            Summary = canProceed
                ? "Smart reinstall preflight passed."
                : "Smart reinstall preflight blocked the reinstall."
        };
    }

    private SmartReinstallSettings ReadSmartReinstallSettings()
    {
        var username =
            (configuration["Reinstall:TemplateUsername"] ?? "Administrator").Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            username = "Administrator";
        }

        return new SmartReinstallSettings(
            configuration.GetValue("Reinstall:ConfigureWindowsGuest", true),
            username,
            configuration["Reinstall:TemplatePassword"] ?? string.Empty,
            configuration.GetValue("Reinstall:GenerateNewWindowsPassword", true),
            Math.Clamp(
                configuration.GetValue(
                    "Reinstall:PowerShellDirectTimeoutSeconds",
                    240),
                60,
                900));
    }

    private sealed record SmartReinstallSettings(
        bool ConfigureWindowsGuest,
        string TemplateUsername,
        string TemplatePassword,
        bool GenerateNewWindowsPassword,
        int PowerShellDirectTimeoutSeconds);

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

    public async Task<VmReinstallRollbackInfo?> GetReinstallRollbackAsync(
        PanelUser? currentUser,
        string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(
            currentUser,
            currentUser!.EffectivePermissions.CanRequestReinstall,
            "Reinstall is not enabled for your account.");

        await _provisioningLock.WaitAsync();
        try
        {
            await CleanupExpiredReinstallRollbackCoreAsync(vmName);
            return await db.GetAvailableVmReinstallRollbackAsync(vmName);
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    public async Task<VmReinstallRollbackRestoreResult> RestoreReinstallRollbackAsync(
        PanelUser? currentUser,
        string vmName,
        long rollbackId,
        Func<int, string, Task>? progress = null)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(
            currentUser,
            currentUser!.EffectivePermissions.CanRequestReinstall,
            "Reinstall is not enabled for your account.");

        if (!currentUser.IsAdmin && !AllowClientReinstallRollbackRestore)
        {
            throw new UnauthorizedAccessException(
                "Previous OS restore is disabled for client accounts.");
        }

        await _provisioningLock.WaitAsync();
        try
        {
            await CleanupExpiredReinstallRollbackCoreAsync(vmName);

            var secret = await db.GetVmReinstallRollbackSecretAsync(rollbackId)
                ?? throw new KeyNotFoundException(
                    "The selected previous OS rollback no longer exists.");

            var rollback = secret.Info;
            if (!string.Equals(
                    rollback.VmName,
                    vmName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The selected rollback belongs to another VPS.");
            }

            if (!string.Equals(
                    rollback.Status,
                    "available",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "This previous OS rollback is no longer available.");
            }

            if (rollback.IsExpired)
            {
                throw new InvalidOperationException(
                    "This previous OS rollback has expired.");
            }

            if (progress is not null)
            {
                await progress(10, "Checking the protected previous OS disk...");
            }

            if (string.IsNullOrWhiteSpace(rollback.RollbackDiskPath)
                || !File.Exists(rollback.RollbackDiskPath))
            {
                await db.MarkVmReinstallRollbackDeletedAsync(
                    rollback.Id,
                    "missing",
                    "Protected rollback VHD/VHDX is missing.");

                throw new InvalidOperationException(
                    "The protected previous OS disk is missing.");
            }

            if (progress is not null)
            {
                await progress(25, "Stopping the VPS and switching back to the previous OS...");
            }

            var diskRestore = await powerShell.RestoreReinstallRollbackDiskAsync(
                vmName,
                rollback.ReinstalledDiskPath,
                rollback.RollbackDiskPath,
                startAfter: true);

            var warnings = new List<string>();

            if (progress is not null)
            {
                await progress(62, "Restoring the previous Windows login and network settings...");
            }

            await db.SaveVmAccessInfoAsync(
                secret.PreviousAccess with
                {
                    VmName = vmName,
                    UpdatedAt = DateTime.UtcNow
                });

            var profile = NormalizeNetworkProfile(
                await db.GetVmNetworkPortProfileAsync(vmName));

            try
            {
                if (profile.HasRdpRule
                    || profile.HasFiveMRule
                    || profile.HasGuestDnsSettings
                    || !string.IsNullOrWhiteSpace(profile.ExtraTcpPorts)
                    || !string.IsNullOrWhiteSpace(profile.ExtraUdpPorts))
                {
                    _ = await powerShell.ApplyNetworkPortProfileAsync(profile);
                }
            }
            catch (Exception hostEx)
            {
                warnings.Add(
                    "Previous OS was restored, but the Hyper-V host NAT/firewall refresh needs attention: "
                    + hostEx.Message);
            }

            if (!string.IsNullOrWhiteSpace(secret.PreviousAccess.Username)
                && !string.IsNullOrWhiteSpace(secret.PreviousAccess.Password))
            {
                try
                {
                    await powerShell.WaitForPowerShellDirectAsync(
                        vmName,
                        secret.PreviousAccess.Username,
                        secret.PreviousAccess.Password,
                        120);

                    _ = await powerShell.RepairWindowsGuestNetworkAsync(
                        profile,
                        secret.PreviousAccess.Username,
                        secret.PreviousAccess.Password,
                        PanelPublicUrl);

                    if (profile.EnableRdp)
                    {
                        var desiredPort =
                            profile.RdpInternalPort > 0
                                ? profile.RdpInternalPort
                                : profile.RdpExternalPort is > 0
                                    ? profile.RdpExternalPort.Value
                                    : 3389;

                        _ = await powerShell.SetWindowsGuestRdpPortAsync(
                            vmName,
                            secret.PreviousAccess.Username,
                            secret.PreviousAccess.Password,
                            desiredPort);
                    }

                    _ = await powerShell.SyncWindowsGuestFirewallRulesAsync(
                        profile,
                        secret.PreviousAccess.Username,
                        secret.PreviousAccess.Password);

                    if (AgentlessRecoveryEnabled)
                    {
                        var recoveryPassword = GenerateRecoveryPassword();
                        await powerShell.EnsureWindowsRecoveryAccountAsync(
                            vmName,
                            secret.PreviousAccess.Username,
                            secret.PreviousAccess.Password,
                            RecoveryAccountUsername,
                            recoveryPassword);

                        var now = DateTime.UtcNow;
                        await db.SaveVmRecoveryAccountAsync(
                            new VmRecoveryAccountSecret(
                                vmName,
                                true,
                                RecoveryAccountUsername,
                                recoveryPassword,
                                now,
                                now,
                                string.Empty,
                                now));
                    }
                }
                catch (Exception guestEx)
                {
                    warnings.Add(
                        "Previous OS is running, but PenguinPanel could not fully refresh its Windows guest settings: "
                        + guestEx.Message);
                }
            }
            else
            {
                warnings.Add(
                    "Previous OS is running, but its old Windows password was not available for automatic guest verification.");
            }

            if (progress is not null)
            {
                await progress(90, "Finalizing previous OS restore...");
            }

            await db.MarkVmReinstallRollbackRestoredAsync(
                rollback.Id,
                warnings.Count == 0
                    ? string.Empty
                    : string.Join(" | ", warnings));

            var finalState = await powerShell.GetVmStateAsync(vmName);
            var restoredInfo = rollback with
            {
                Status = "restored",
                RestoredAt = DateTime.UtcNow,
                LastError = warnings.Count == 0
                    ? string.Empty
                    : string.Join(" | ", warnings)
            };

            await db.AddAuditAsync(
                "VM_REINSTALL_ROLLBACK_RESTORED",
                "vm",
                vmName,
                $"Previous OS rollback #{rollback.Id} restored by {currentUser.Username}. Previous protected disk is now active; reinstalled disk {diskRestore.RemovedReinstalledDiskPath} was removed after successful Hyper-V boot. Warnings={warnings.Count}.");

            await NotifyAssignedUsersAsync(
                vmName,
                $"PenguinPanel: previous OS restored for {vmName}",
                $"The previous Windows installation for {vmName} was restored successfully. Open Access in PenguinPanel for the restored Windows login details."
                + (warnings.Count == 0
                    ? string.Empty
                    : $"\n\nPenguinPanel recorded {warnings.Count} warning(s); support may need to review guest/network settings."));

            if (progress is not null)
            {
                await progress(100, "Previous OS restored successfully.");
            }

            return new VmReinstallRollbackRestoreResult(
                restoredInfo,
                finalState,
                warnings,
                warnings.Count == 0
                    ? "Previous Windows installation restored successfully. Your previous login details are back on Access."
                    : $"Previous Windows installation restored and started. {warnings.Count} warning(s) need attention.");
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    public async Task<VmReinstallRollbackInfo> SetReinstallRollbackRetentionAsync(
        PanelUser? currentUser,
        string vmName,
        long rollbackId,
        int days)
    {
        RequireAdmin(currentUser);
        await EnsureCanAccessVmAsync(currentUser, vmName);

        days = Math.Clamp(days, 1, ReinstallRollbackMaxRetentionDays);

        await _provisioningLock.WaitAsync();
        try
        {
            var secret = await db.GetVmReinstallRollbackSecretAsync(rollbackId)
                ?? throw new KeyNotFoundException("Rollback no longer exists.");

            if (!string.Equals(
                    secret.Info.VmName,
                    vmName,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    secret.Info.Status,
                    "available",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Only the active rollback for this VPS can be retained.");
            }

            var expiresAt = DateTime.UtcNow.AddDays(days);
            await db.SetVmReinstallRollbackExpiryAsync(
                rollbackId,
                expiresAt);

            await db.AddAuditAsync(
                "VM_REINSTALL_ROLLBACK_RETENTION_CHANGED",
                "vm",
                vmName,
                $"Rollback #{rollbackId} retained until {expiresAt:o} by {currentUser!.Username}.");

            return secret.Info with { ExpiresAt = expiresAt };
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    public async Task DeleteReinstallRollbackAsync(
        PanelUser? currentUser,
        string vmName,
        long rollbackId)
    {
        RequireAdmin(currentUser);
        await EnsureCanAccessVmAsync(currentUser, vmName);

        await _provisioningLock.WaitAsync();
        try
        {
            var secret = await db.GetVmReinstallRollbackSecretAsync(rollbackId)
                ?? throw new KeyNotFoundException("Rollback no longer exists.");

            var rollback = secret.Info;
            if (!string.Equals(
                    rollback.VmName,
                    vmName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Rollback belongs to another VPS.");
            }

            if (!string.Equals(
                    rollback.Status,
                    "available",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Only an available rollback can be deleted.");
            }

            await powerShell.DeleteReinstallRollbackDiskAsync(
                vmName,
                rollback.RollbackDiskPath);

            await db.MarkVmReinstallRollbackDeletedAsync(
                rollback.Id,
                "deleted");

            await db.AddAuditAsync(
                "VM_REINSTALL_ROLLBACK_DELETED",
                "vm",
                vmName,
                $"Rollback #{rollback.Id} deleted by {currentUser!.Username}. Freed approximately {rollback.PhysicalDiskBytes} bytes.");
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    public async Task<VmReinstallRollbackCleanupResult> CleanupExpiredReinstallRollbacksAsync(
        CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Clamp(
            configuration.GetValue("ReinstallRollback:CleanupBatchSize", 500),
            1,
            2000);

        await _provisioningLock.WaitAsync(cancellationToken);
        try
        {
            var expired = await db.GetExpiredVmReinstallRollbacksAsync(
                DateTime.UtcNow,
                batchSize);

            var deleted = 0;
            var failed = 0;
            long freedBytes = 0;

            foreach (var rollback in expired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await CleanupExpiredReinstallRollbackRecordCoreAsync(rollback))
                {
                    deleted++;
                    var rollbackBytes = Math.Max(0, rollback.PhysicalDiskBytes);
                    freedBytes = rollbackBytes > long.MaxValue - freedBytes
                        ? long.MaxValue
                        : freedBytes + rollbackBytes;
                }
                else
                {
                    failed++;
                }
            }

            return new VmReinstallRollbackCleanupResult(
                expired.Count,
                deleted,
                failed,
                freedBytes,
                DateTime.UtcNow);
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    private async Task CleanupExpiredReinstallRollbackCoreAsync(
        string vmName)
    {
        var rollback = await db.GetAvailableVmReinstallRollbackAsync(vmName);
        if (rollback is null || !rollback.IsExpired)
        {
            return;
        }

        await CleanupExpiredReinstallRollbackRecordCoreAsync(rollback);
    }

    private async Task<bool> CleanupExpiredReinstallRollbackRecordCoreAsync(
        VmReinstallRollbackInfo rollback)
    {
        try
        {
            await powerShell.DeleteReinstallRollbackDiskAsync(
                rollback.VmName,
                rollback.RollbackDiskPath);

            await db.MarkVmReinstallRollbackDeletedAsync(
                rollback.Id,
                "expired");

            await db.AddAuditAsync(
                "VM_REINSTALL_ROLLBACK_EXPIRED",
                "vm",
                rollback.VmName,
                $"Rollback #{rollback.Id} expired at {rollback.ExpiresAt:o} and its protected disk was cleaned automatically.");
            return true;
        }
        catch (Exception ex)
        {
            await db.SetVmReinstallRollbackErrorAsync(
                rollback.Id,
                "Automatic expiry cleanup failed: " + ex.Message);

            await db.AddAuditAsync(
                "VM_REINSTALL_ROLLBACK_EXPIRY_CLEANUP_FAILED",
                "vm",
                rollback.VmName,
                $"Rollback #{rollback.Id}: {ex.Message}");
            return false;
        }
    }

    public async Task<IReadOnlyList<VmFullBackupInfo>> GetFullBackupsAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");
        return await db.GetVmBackupsAsync(vmName);
    }

    public async Task<VmBackupStorageStatus> GetBackupStorageStatusAsync(PanelUser? currentUser)
    {
        if (currentUser is null)
        {
            throw new UnauthorizedAccessException("You must be signed in.");
        }

        var settings = ReadBackupSettings();
        var statuses = ReadBackupStorageRootStatuses(settings);
        var available = statuses.Where(item => item.IsAvailable).ToList();
        var recommended = SelectBackupStorageRoot(settings, available, requiredBytes: 0, throwIfNone: false);

        if (recommended is not null)
        {
            return new VmBackupStorageStatus(
                recommended.Path,
                true,
                recommended.FreeBytes,
                recommended.TotalBytes,
                recommended.ReserveBytes,
                settings.MaxBackupsPerVm,
                string.Empty,
                settings.SelectionMode,
                statuses);
        }

        var error = statuses.Count == 0
            ? "No backup storage roots are configured."
            : string.Join("; ", statuses.Select(item =>
                item.IsAvailable
                    ? $"{item.Path}: available"
                    : $"{item.Path}: {item.Error}"));

        return new VmBackupStorageStatus(
            settings.DefaultRoot,
            false,
            0,
            0,
            0,
            settings.MaxBackupsPerVm,
            error,
            settings.SelectionMode,
            statuses);
    }

    public async Task<VmFullBackupResult> CreateFullBackupAsync(
        PanelUser? currentUser,
        string vmName,
        string backupName,
        Func<int, string, Task>? progress = null)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");

        await _provisioningLock.WaitAsync();
        try
        {
            return await CreateFullBackupCoreAsync(
                currentUser!,
                vmName,
                backupName,
                "manual",
                false,
                true,
                progress);
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    public async Task<VmFullBackupRestoreResult> RestoreFullBackupAsync(
        PanelUser? currentUser,
        string vmName,
        long backupId,
        string confirmation,
        Func<int, string, Task>? progress = null)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");

        if (!string.Equals((confirmation ?? string.Empty).Trim(), "RESTORE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Type RESTORE exactly to confirm a full backup restore.");
        }

        await _provisioningLock.WaitAsync();
        try
        {
            var selected = await db.GetVmBackupAsync(backupId)
                ?? throw new KeyNotFoundException("The selected full backup no longer exists.");

            if (!string.Equals(selected.VmName, vmName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The selected backup belongs to another VM.");
            }

            if (!string.Equals(selected.Status, "complete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Backup '{selected.Name}' is not complete and cannot be restored.");
            }

            var settings = ReadBackupSettings();
            EnsureBackupPathIsSafe(selected.BackupPath, settings.Roots);
            EnsureBackupPathIsSafe(selected.ManifestPath, settings.Roots);

            if (!File.Exists(selected.ManifestPath))
            {
                throw new InvalidOperationException("Backup manifest is missing.");
            }

            var manifestJson = await File.ReadAllTextAsync(selected.ManifestPath);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<VmBackupManifest>(
                manifestJson,
                BackupJsonOptions)
                ?? throw new InvalidOperationException("Backup manifest is invalid.");

            if (!string.Equals(manifest.VmName, vmName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Backup manifest VM name does not match this VPS.");
            }

            if (manifest.Disks.Count == 0)
            {
                throw new InvalidOperationException("Backup manifest contains no disks.");
            }

            foreach (var disk in manifest.Disks)
            {
                var backupDisk = Path.GetFullPath(Path.Combine(selected.BackupPath, disk.RelativePath));
                EnsureBackupPathIsSafe(backupDisk, selected.BackupPath);
                if (!File.Exists(backupDisk))
                {
                    throw new InvalidOperationException($"Backup disk is missing: {disk.RelativePath}");
                }
            }

            var current = await powerShell.GetVmBackupSourceAsync(vmName);
            if (current.CheckpointCount > 0)
            {
                throw new InvalidOperationException(
                    "Full backup restore is blocked while Hyper-V checkpoints exist. Delete/merge the checkpoints first, then restore the full backup.");
            }

            if (current.Disks.Count != manifest.Disks.Count)
            {
                throw new InvalidOperationException(
                    $"Disk layout changed since the backup. Backup has {manifest.Disks.Count} disk(s); current VM has {current.Disks.Count}.");
            }

            foreach (var backupDisk in manifest.Disks)
            {
                var match = current.Disks.Any(currentDisk =>
                    string.Equals(currentDisk.ControllerType, backupDisk.ControllerType, StringComparison.OrdinalIgnoreCase)
                    && currentDisk.ControllerNumber == backupDisk.ControllerNumber
                    && currentDisk.ControllerLocation == backupDisk.ControllerLocation);
                if (!match)
                {
                    throw new InvalidOperationException(
                        $"Current VM no longer has backup disk slot {backupDisk.ControllerType}:{backupDisk.ControllerNumber}:{backupDisk.ControllerLocation}.");
                }
            }

            var previousState = current.State;
            var wasRunning = IsRunningLikeState(previousState);

            if (progress is not null) await progress(8, "Preparing restore and checking current VM state...");

            if (wasRunning)
            {
                if (progress is not null) await progress(14, "Stopping VM before safety backup and restore...");
                await availability.MarkPlannedDowntimeAsync(vmName, $"Full backup restore by {currentUser!.Username}", TimeSpan.FromHours(4));
                await powerShell.StopVmForCheckpointRestoreAsync(vmName);
            }

            VmFullBackupResult safetyBackup;
            try
            {
                if (progress is not null) await progress(20, "Creating automatic pre-restore safety backup...");
                safetyBackup = await CreateFullBackupCoreAsync(
                    currentUser!,
                    vmName,
                    $"Before restore {selected.Name} {DateTime.Now:yyyy-MM-dd HH-mm}",
                    "pre-restore",
                    true,
                    false,
                    async (value, text) =>
                    {
                        if (progress is not null)
                        {
                            var mapped = 20 + (int)Math.Round(value * 0.35);
                            await progress(Math.Clamp(mapped, 20, 55), "Safety backup: " + text);
                        }
                    });

                if (progress is not null) await progress(60, "Safety backup complete. Validating selected backup disks...");

                var restoreRequests = manifest.Disks.Select(disk =>
                {
                    var backupDiskPath = Path.GetFullPath(Path.Combine(selected.BackupPath, disk.RelativePath));
                    return new VmBackupDiskRestoreRequest(
                        backupDiskPath,
                        disk.ControllerType,
                        disk.ControllerNumber,
                        disk.ControllerLocation);
                }).ToList();

                if (progress is not null) await progress(68, "Restoring full backup to the VM disks...");
                await powerShell.RestoreVmDisksFromBackupAsync(vmName, restoreRequests);

                if (progress is not null) await progress(88, wasRunning ? "Restore complete. Starting VM..." : "Restore complete. VM will remain stopped.");

                if (wasRunning)
                {
                    await powerShell.StartVmAsync(vmName);
                }

                var finalState = await powerShell.GetVmStateAsync(vmName);

                await db.AddAuditAsync(
                    "VM_FULL_BACKUP_RESTORED",
                    "vm",
                    vmName,
                    $"Backup #{selected.Id} '{selected.Name}' restored by {currentUser!.Username}. Automatic safety backup #{safetyBackup.Backup.Id} '{safetyBackup.Backup.Name}' created first. Previous state={previousState}; final state={finalState}.");

                if (progress is not null) await progress(100, "Full backup restore completed.");

                return new VmFullBackupRestoreResult(
                    vmName,
                    selected.Id,
                    selected.Name,
                    safetyBackup.Backup.Id,
                    safetyBackup.Backup.Name,
                    wasRunning,
                    previousState,
                    finalState,
                    $"Full backup '{selected.Name}' restored. Safety backup '{safetyBackup.Backup.Name}' was created first.");
            }
            catch
            {
                if (wasRunning)
                {
                    try
                    {
                        var state = await powerShell.GetVmStateAsync(vmName);
                        if (!IsRunningLikeState(state))
                        {
                            await powerShell.StartVmAsync(vmName);
                        }
                    }
                    catch
                    {
                        // Preserve the original restore error.
                    }
                }

                throw;
            }
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    public async Task DeleteFullBackupAsync(PanelUser? currentUser, string vmName, long backupId)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");

        await _provisioningLock.WaitAsync();
        try
        {
            var backup = await db.GetVmBackupAsync(backupId)
                ?? throw new KeyNotFoundException("Backup no longer exists.");

            if (!string.Equals(backup.VmName, vmName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Backup belongs to another VM.");
            }

            if (string.Equals(backup.Status, "creating", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A backup that is still being created cannot be deleted.");
            }

            var settings = ReadBackupSettings();
            EnsureBackupPathIsSafe(backup.BackupPath, settings.Roots);

            var current = await powerShell.GetVmBackupSourceAsync(vmName);
            foreach (var disk in current.Disks)
            {
                if (IsPathInsideStorageRoot(disk.Path, backup.BackupPath))
                {
                    throw new InvalidOperationException("Backup folder unexpectedly contains an active VM disk. Deletion was blocked.");
                }
            }

            if (Directory.Exists(backup.BackupPath))
            {
                Directory.Delete(backup.BackupPath, recursive: true);
            }

            await db.DeleteVmBackupRecordAsync(backupId);
            await db.AddAuditAsync(
                "VM_FULL_BACKUP_DELETED",
                "vm",
                vmName,
                $"Backup #{backup.Id} '{backup.Name}' deleted by {currentUser!.Username}.");
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    private async Task<VmFullBackupResult> CreateFullBackupCoreAsync(
        PanelUser currentUser,
        string vmName,
        string backupName,
        string backupType,
        bool vmAlreadyStopped,
        bool restartAfter,
        Func<int, string, Task>? progress)
    {
        var settings = ReadBackupSettings();
        ValidateBackupRoots(settings.Roots);

        if (string.Equals(backupType, "manual", StringComparison.OrdinalIgnoreCase))
        {
            var existing = await db.CountCompletedVmBackupsAsync(vmName, "manual");
            if (existing >= settings.MaxBackupsPerVm)
            {
                throw new InvalidOperationException(
                    $"This VPS already has {existing} manual full backup(s). The configured limit is {settings.MaxBackupsPerVm}. Delete an older backup before creating another.");
            }
        }

        var source = await powerShell.GetVmBackupSourceAsync(vmName);
        if (source.Disks.Count == 0)
        {
            throw new InvalidOperationException("No virtual hard disks were found on this VM.");
        }

        var wasRunning = IsRunningLikeState(source.State);
        var estimatedBytes = source.Disks.Sum(item => Math.Max(item.EstimatedBackupBytes, item.PhysicalSizeBytes));
        var selectedBackupRoot = ResolveBackupStorageRoot(settings, estimatedBytes);

        if (progress is not null)
        {
            await progress(
                3,
                $"Smart storage selected {selectedBackupRoot} for this backup (estimated {FormatBytesAsGb(estimatedBytes)} GB)." );
        }

        backupName = NormalizeBackupName(
            string.IsNullOrWhiteSpace(backupName)
                ? $"Full backup {DateTime.Now:yyyy-MM-dd HH-mm}"
                : backupName);

        var safeVm = SanitizeBackupPathSegment(vmName);
        var safeName = SanitizeBackupPathSegment(backupName);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var folder = Path.GetFullPath(Path.Combine(
            selectedBackupRoot,
            safeVm,
            $"{timestamp}-{Guid.NewGuid():N}-{safeName}"));
        EnsureBackupPathIsSafe(folder, settings.Roots);

        var manifestPath = Path.Combine(folder, "manifest.json");
        var record = new VmFullBackupInfo(
            0,
            vmName,
            backupName,
            backupType,
            "creating",
            folder,
            manifestPath,
            0,
            source.Disks.Count,
            wasRunning,
            currentUser.Id,
            currentUser.Username,
            string.Empty,
            DateTime.UtcNow,
            null);

        var backupId = await db.CreateVmBackupRecordAsync(record);
        var stoppedByBackup = false;

        try
        {
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(folder, "disks"));

            if (progress is not null) await progress(5, "Backup record created. Preparing VM...");

            if (!vmAlreadyStopped && wasRunning)
            {
                if (progress is not null) await progress(10, "Stopping VM for a consistent full-disk backup...");
                await availability.MarkPlannedDowntimeAsync(vmName, $"Full backup by {currentUser.Username}", TimeSpan.FromHours(2));
                await powerShell.StopVmForCheckpointRestoreAsync(vmName);
                stoppedByBackup = true;
            }

            var manifestDisks = new List<VmBackupDiskManifest>();
            long totalBytes = 0;

            for (var index = 0; index < source.Disks.Count; index++)
            {
                var disk = source.Disks[index];
                var extension = disk.IsDifferencing
                    ? ".vhdx"
                    : Path.GetExtension(disk.Path).ToLowerInvariant();
                if (extension is not ".vhd" and not ".vhdx")
                {
                    extension = ".vhdx";
                }

                var fileName = $"{index + 1:D2}-{SanitizeBackupPathSegment(Path.GetFileNameWithoutExtension(disk.Path))}{extension}";
                var relativePath = Path.Combine("disks", fileName);
                var destination = Path.Combine(folder, relativePath);

                if (progress is not null)
                {
                    var pct = 15 + (int)Math.Round((index / (double)Math.Max(1, source.Disks.Count)) * 70);
                    var special = disk.IsDifferencing ? " (merging checkpoint chain into standalone VHDX)" : string.Empty;
                    await progress(pct, $"Copying disk {index + 1}/{source.Disks.Count}{special}...");
                }

                var copied = await powerShell.CreateStandaloneVmDiskBackupAsync(disk.Path, destination);
                totalBytes += copied.FileSizeBytes;

                manifestDisks.Add(new VmBackupDiskManifest(
                    relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    disk.Path,
                    disk.ControllerType,
                    disk.ControllerNumber,
                    disk.ControllerLocation,
                    copied.FileSizeBytes,
                    copied.VirtualSizeBytes));
            }

            var manifest = new VmBackupManifest(
                1,
                vmName,
                backupName,
                backupType,
                source.State,
                source.CpuCores,
                source.MemoryStartupBytes,
                source.Generation,
                manifestDisks,
                DateTime.UtcNow);

            var json = System.Text.Json.JsonSerializer.Serialize(manifest, BackupJsonOptions);
            await File.WriteAllTextAsync(manifestPath, json);

            if (progress is not null) await progress(90, "Verifying backup manifest and copied disk files...");

            foreach (var disk in manifestDisks)
            {
                var path = Path.GetFullPath(Path.Combine(folder, disk.RelativePath));
                EnsureBackupPathIsSafe(path, folder);
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException($"Backup verification failed; disk is missing: {disk.RelativePath}");
                }
            }

            await db.UpdateVmBackupRecordAsync(
                backupId,
                "complete",
                folder,
                manifestPath,
                totalBytes,
                manifestDisks.Count,
                string.Empty,
                DateTime.UtcNow);

            var completed = await db.GetVmBackupAsync(backupId)
                ?? throw new InvalidOperationException("Completed backup could not be reloaded from the database.");

            string restartWarning = string.Empty;
            if (restartAfter && wasRunning)
            {
                try
                {
                    if (progress is not null) await progress(95, "Backup complete. Starting VM again...");
                    await powerShell.StartVmAsync(vmName);
                }
                catch (Exception ex)
                {
                    restartWarning = " Backup is complete, but the VM could not be restarted automatically: " + ex.Message;
                }
            }

            await db.AddAuditAsync(
                "VM_FULL_BACKUP_CREATED",
                "vm",
                vmName,
                $"Backup #{backupId} '{backupName}' ({backupType}) created by {currentUser.Username}; root={selectedBackupRoot}; disks={manifestDisks.Count}; size={FormatBytesAsGb(totalBytes)} GB; checkpoints={source.CheckpointCount}.{restartWarning}");

            if (progress is not null) await progress(100, "Full backup completed.");

            return new VmFullBackupResult(
                completed,
                $"Full backup '{backupName}' created successfully.{restartWarning}");
        }
        catch (Exception ex)
        {
            await db.UpdateVmBackupRecordAsync(
                backupId,
                "failed",
                folder,
                manifestPath,
                0,
                source.Disks.Count,
                ex.Message,
                DateTime.UtcNow);

            await db.AddAuditAsync(
                "VM_FULL_BACKUP_FAILED",
                "vm",
                vmName,
                $"Backup #{backupId} '{backupName}' ({backupType}) failed: {ex.Message}");

            if ((stoppedByBackup || vmAlreadyStopped) && restartAfter && wasRunning)
            {
                try
                {
                    var state = await powerShell.GetVmStateAsync(vmName);
                    if (!IsRunningLikeState(state))
                    {
                        await powerShell.StartVmAsync(vmName);
                    }
                }
                catch
                {
                    // Preserve the original backup error.
                }
            }

            throw;
        }
    }

    private BackupSettings ReadBackupSettings()
    {
        var defaultRoot = Path.GetFullPath(
            (configuration["Backup:Root"] ?? @"D:\PenguinBackups").Trim());

        var configuredRoots = configuration
            .GetSection("Backup:Roots")
            .GetChildren()
            .Select(item => (item.Value ?? string.Empty).Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(Path.GetFullPath)
            .ToList();

        if (configuredRoots.Count == 0)
        {
            configuredRoots.Add(defaultRoot);
        }
        else if (!configuredRoots.Contains(defaultRoot, StringComparer.OrdinalIgnoreCase))
        {
            configuredRoots.Insert(0, defaultRoot);
        }

        configuredRoots = configuredRoots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selectionMode = (configuration["Backup:StorageSelectionMode"] ?? "MostFreeSpace").Trim();
        if (!selectionMode.Equals("MostFreeSpace", StringComparison.OrdinalIgnoreCase)
            && !selectionMode.Equals("MostFreePercent", StringComparison.OrdinalIgnoreCase)
            && !selectionMode.Equals("DefaultFirst", StringComparison.OrdinalIgnoreCase))
        {
            selectionMode = "MostFreeSpace";
        }

        return new BackupSettings(
            defaultRoot,
            configuredRoots,
            selectionMode,
            Math.Clamp(configuration.GetValue("Backup:MinimumFreeSpaceAfterBackupGb", 100), 0, 100000),
            Math.Clamp(configuration.GetValue("Backup:MinimumFreeSpaceAfterBackupPercent", 5), 0, 50),
            Math.Clamp(configuration.GetValue("Backup:MaxBackupsPerVm", 5), 1, 100));
    }

    private void ValidateBackupRoots(IReadOnlyList<string> backupRoots)
    {
        if (backupRoots.Count == 0)
        {
            throw new InvalidOperationException("No backup storage roots are configured.");
        }

        foreach (var backupRoot in backupRoots)
        {
            ValidateBackupRoot(backupRoot);
        }
    }

    private void ValidateBackupRoot(string backupRoot)
    {
        var fullBackupRoot = Path.GetFullPath(backupRoot);
        var volumeRoot = Path.GetPathRoot(fullBackupRoot);
        if (string.IsNullOrWhiteSpace(volumeRoot) || volumeRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Backup root '{fullBackupRoot}' must currently be a local Windows drive path.");
        }

        var defaults = ReadVmProvisioningDefaults();
        foreach (var vmRoot in defaults.VmStorageRoots)
        {
            var fullVmRoot = Path.GetFullPath(vmRoot);
            if (IsPathInsideStorageRoot(fullBackupRoot, fullVmRoot)
                || IsPathInsideStorageRoot(fullVmRoot, fullBackupRoot))
            {
                throw new InvalidOperationException(
                    $"Backup root '{fullBackupRoot}' must be a separate sibling location, not inside or above VM storage root '{fullVmRoot}'. Example: D:\\Vms + D:\\PenguinBackups is safe.");
            }
        }

        Directory.CreateDirectory(fullBackupRoot);
    }

    private IReadOnlyList<VmBackupStorageRootStatus> ReadBackupStorageRootStatuses(BackupSettings settings)
    {
        var result = new List<VmBackupStorageRootStatus>();

        foreach (var configuredRoot in settings.Roots)
        {
            var path = Path.GetFullPath(configuredRoot);
            var isDefault = string.Equals(path, settings.DefaultRoot, StringComparison.OrdinalIgnoreCase);

            try
            {
                ValidateBackupRoot(path);
                var volumeRoot = Path.GetPathRoot(path)
                    ?? throw new InvalidOperationException("Could not determine the backup volume.");
                var drive = new DriveInfo(volumeRoot);
                if (!drive.IsReady)
                {
                    result.Add(new VmBackupStorageRootStatus(
                        path, isDefault, false, 0, 0, 0, $"Drive {volumeRoot} is not ready."));
                    continue;
                }

                var reserve = CalculateBackupReserveBytes(
                    drive.TotalSize,
                    settings.MinimumFreeSpaceGb,
                    settings.MinimumFreeSpacePercent);

                result.Add(new VmBackupStorageRootStatus(
                    path,
                    isDefault,
                    true,
                    drive.AvailableFreeSpace,
                    drive.TotalSize,
                    reserve,
                    string.Empty));
            }
            catch (Exception ex)
            {
                result.Add(new VmBackupStorageRootStatus(
                    path, isDefault, false, 0, 0, 0, ex.Message));
            }
        }

        return result;
    }

    private string ResolveBackupStorageRoot(BackupSettings settings, long requiredBytes)
    {
        var statuses = ReadBackupStorageRootStatuses(settings);
        var selected = SelectBackupStorageRoot(settings, statuses, requiredBytes, throwIfNone: true);
        return selected!.Path;
    }

    private VmBackupStorageRootStatus? SelectBackupStorageRoot(
        BackupSettings settings,
        IReadOnlyList<VmBackupStorageRootStatus> statuses,
        long requiredBytes,
        bool throwIfNone)
    {
        var available = statuses
            .Where(item =>
                item.IsAvailable
                && item.FreeBytes >= checked(requiredBytes + item.ReserveBytes))
            .ToList();

        if (available.Count == 0)
        {
            if (!throwIfNone)
            {
                return null;
            }

            var summary = statuses.Count == 0
                ? "No backup roots configured."
                : string.Join("; ", statuses.Select(FormatBackupStorageRootSummary));

            throw new InvalidOperationException(
                $"No approved backup storage root has enough free space for the estimated {FormatBytesAsGb(requiredBytes)} GB backup while keeping the configured safety reserve. {summary}");
        }

        if (settings.SelectionMode.Equals("DefaultFirst", StringComparison.OrdinalIgnoreCase))
        {
            return available.FirstOrDefault(item => item.IsDefault)
                ?? available.OrderByDescending(item => item.FreeBytes).First();
        }

        if (settings.SelectionMode.Equals("MostFreePercent", StringComparison.OrdinalIgnoreCase))
        {
            return available
                .OrderByDescending(item => item.TotalBytes > 0 ? (double)item.FreeBytes / item.TotalBytes : 0d)
                .ThenByDescending(item => item.FreeBytes)
                .First();
        }

        // MostFreeSpace mirrors the normal VPS smart-storage default.
        return available
            .OrderByDescending(item => item.FreeBytes)
            .ThenByDescending(item => item.IsDefault)
            .First();
    }

    private static string FormatBackupStorageRootSummary(VmBackupStorageRootStatus status) =>
        status.IsAvailable
            ? $"{status.Path}: {FormatBytesAsGb(status.FreeBytes)} GB free, reserve {FormatBytesAsGb(status.ReserveBytes)} GB"
            : $"{status.Path}: unavailable ({status.Error})";

    private static long CalculateBackupReserveBytes(long totalBytes, int reserveGb, int reservePercent)
    {
        var gbReserve = checked((long)reserveGb * 1024L * 1024L * 1024L);
        var percentReserve = totalBytes > 0 && reservePercent > 0
            ? (long)Math.Ceiling(totalBytes * (reservePercent / 100d))
            : 0L;
        return Math.Max(gbReserve, percentReserve);
    }

    private static string NormalizeBackupName(string value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0) throw new InvalidOperationException("Backup name is required.");
        if (name.Length > 120) name = name[..120];
        return name;
    }

    private static string SanitizeBackupPathSegment(string value)
    {
        var text = (value ?? string.Empty).Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            text = text.Replace(invalid, '-');
        }

        text = text.Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(text)) text = "backup";
        return text.Length > 80 ? text[..80] : text;
    }

    private static void EnsureBackupPathIsSafe(string path, string root)
    {
        if (!IsPathInsideStorageRoot(path, root))
        {
            throw new InvalidOperationException($"Backup path escaped its configured root and was blocked: {path}");
        }
    }

    private static void EnsureBackupPathIsSafe(string path, IReadOnlyList<string> roots)
    {
        if (!roots.Any(root => IsPathInsideStorageRoot(path, root)))
        {
            throw new InvalidOperationException(
                $"Backup path is outside every configured backup storage root and was blocked: {path}");
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions BackupJsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

    private sealed record BackupSettings(
        string DefaultRoot,
        IReadOnlyList<string> Roots,
        string SelectionMode,
        int MinimumFreeSpaceGb,
        int MinimumFreeSpacePercent,
        int MaxBackupsPerVm);

    public async Task<IReadOnlyList<VmCheckpointInfo>> GetCheckpointsAsync(PanelUser? currentUser, string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");
        return await powerShell.GetVmCheckpointsAsync(vmName);
    }

    public async Task<IReadOnlyList<VmQuickRestorePointInfo>> GetQuickRestorePointsAsync(
        PanelUser? currentUser,
        string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");

        var settings = ReadQuickSnapshotSettings();
        var snapshots = await powerShell.GetVmCheckpointsAsync(vmName);
        var metadata = (await db.GetQuickSnapshotMetadataAsync(vmName))
            .GroupBy(item => item.SnapshotId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        return snapshots.Select(snapshot =>
        {
            metadata.TryGetValue(snapshot.Id, out var meta);

            var pinned = meta?.IsPinned == true;
            var temporary = meta?.IsTemporarySafety == true;
            DateTime? expiresAt = meta?.ExpiresAt;

            if (!temporary && expiresAt is null && settings.MaxAgeDays > 0)
            {
                expiresAt = snapshot.CreationTime.ToUniversalTime().AddDays(settings.MaxAgeDays);
            }

            var expired = !pinned && expiresAt is not null && expiresAt.Value <= now;

            return new VmQuickRestorePointInfo(
                snapshot.Name,
                snapshot.Id,
                snapshot.SnapshotType,
                snapshot.CreationTime,
                snapshot.DiskFootprintBytes,
                meta?.Description ?? string.Empty,
                pinned,
                temporary,
                expiresAt,
                expired);
        })
        .OrderByDescending(item => item.CreationTime)
        .ToList();
    }

    public async Task<VmQuickSnapshotStorageStatus> GetQuickSnapshotStorageStatusAsync(
        PanelUser? currentUser,
        string vmName)
    {
        var points = await GetQuickRestorePointsAsync(currentUser, vmName);
        var settings = ReadQuickSnapshotSettings();
        var totalAvhdx = await powerShell.GetVmCheckpointStorageBytesAsync(vmName);

        return new VmQuickSnapshotStorageStatus(
            points.Count,
            points.Count(item => !item.IsTemporarySafety),
            points.Count(item => item.IsTemporarySafety),
            points.Count(item => item.IsExpired && !item.IsPinned),
            settings.MaxPerVm,
            settings.MaxAgeDays,
            settings.SafetyRetentionHours,
            totalAvhdx,
            settings.WarningStorageBytes,
            totalAvhdx >= settings.WarningStorageBytes);
    }

    public async Task<VmQuickRestorePointInfo> CreateQuickRestorePointAsync(
        PanelUser? currentUser,
        string vmName,
        string checkpointName,
        string description)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");

        await _provisioningLock.WaitAsync();
        try
        {
            var settings = ReadQuickSnapshotSettings();
            var existing = await GetQuickRestorePointsAsync(currentUser, vmName);
            var manualCount = existing.Count(item => !item.IsTemporarySafety);

            if (manualCount >= settings.MaxPerVm)
            {
                throw new InvalidOperationException(
                    $"This VPS already has {manualCount} Quick Restore Point(s). The configured limit is {settings.MaxPerVm}. Delete an old unpinned point before creating another.");
            }

            checkpointName = NormalizeQuickSnapshotName(
                string.IsNullOrWhiteSpace(checkpointName)
                    ? $"Restore point {DateTime.Now:yyyy-MM-dd HH-mm}"
                    : checkpointName);

            description = NormalizeQuickSnapshotDescription(description);

            if (existing.Any(item =>
                    string.Equals(item.Name, checkpointName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"A Quick Restore Point named '{checkpointName}' already exists. Choose another name.");
            }

            var created = await powerShell.CreateVmCheckpointAsync(
                vmName,
                checkpointName,
                settings.PreferProduction);

            var now = DateTime.UtcNow;
            await db.UpsertQuickSnapshotMetadataAsync(new VmQuickSnapshotMetadata(
                vmName,
                created.Id,
                created.Name,
                description,
                false,
                false,
                null,
                currentUser!.Id,
                currentUser.Username,
                now,
                now));

            await db.AddAuditAsync(
                "VM_QUICK_RESTORE_POINT_CREATED",
                "vm",
                vmName,
                $"Quick Restore Point '{created.Name}' ({created.SnapshotType}) created by {currentUser.Username}. Description='{description}'.");

            var points = await GetQuickRestorePointsAsync(currentUser, vmName);
            return points.First(item => string.Equals(item.Id, created.Id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    public async Task SetQuickRestorePointPinnedAsync(
        PanelUser? currentUser,
        string vmName,
        string snapshotId,
        bool isPinned)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");

        await _provisioningLock.WaitAsync();
        try
        {
            var points = await GetQuickRestorePointsAsync(currentUser, vmName);
            var point = points.FirstOrDefault(item =>
                string.Equals(item.Id, snapshotId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException("Quick Restore Point no longer exists.");

            if (isPinned && point.IsTemporarySafety)
            {
                throw new InvalidOperationException(
                    "Temporary Safety Restore Points cannot be made permanent. Create or choose a normal restore point instead.");
            }

            var metadata = (await db.GetQuickSnapshotMetadataAsync(vmName))
                .FirstOrDefault(item =>
                    string.Equals(item.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase));

            var now = DateTime.UtcNow;
            if (metadata is null)
            {
                await db.UpsertQuickSnapshotMetadataAsync(new VmQuickSnapshotMetadata(
                    vmName,
                    point.Id,
                    point.Name,
                    point.Description,
                    false,
                    point.IsTemporarySafety,
                    point.ExpiresAt,
                    currentUser!.Id,
                    currentUser.Username,
                    point.CreationTime.ToUniversalTime(),
                    now));
            }

            if (isPinned)
            {
                // Atomic database operation: clear the previous permanent restore point
                // and promote this one inside the same transaction.
                await db.SetPermanentQuickSnapshotAsync(vmName, snapshotId);

                await db.AddAuditAsync(
                    "VM_QUICK_RESTORE_POINT_PERMANENT_SET",
                    "vm",
                    vmName,
                    $"Quick Restore Point '{point.Name}' made the permanent restore point by {currentUser.Username}. Any previous permanent restore point was returned to normal retention.");
            }
            else
            {
                await db.SetQuickSnapshotPinnedAsync(vmName, snapshotId, false);

                await db.AddAuditAsync(
                    "VM_QUICK_RESTORE_POINT_PERMANENT_REMOVED",
                    "vm",
                    vmName,
                    $"Permanent protection removed from Quick Restore Point '{point.Name}' by {currentUser.Username}.");
            }
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    public async Task<QuickRestorePointRestoreResult> RestoreQuickRestorePointAsync(
        PanelUser? currentUser,
        string vmName,
        string snapshotId)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");

        await _provisioningLock.WaitAsync();
        try
        {
            var settings = ReadQuickSnapshotSettings();
            var points = await GetQuickRestorePointsAsync(currentUser, vmName);
            var target = points.FirstOrDefault(item =>
                string.Equals(item.Id, snapshotId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException("The selected Quick Restore Point no longer exists.");

            var previousState = await powerShell.GetVmStateAsync(vmName);
            var wasRunning = IsRunningLikeState(previousState);

            // Equivalent to Hyper-V Manager's "Create Checkpoint and Apply":
            // preserve the CURRENT state in a lightweight temporary safety point
            // before applying the older restore point.
            var safetyBase =
                $"Safety before restore {SanitizeQuickSnapshotSafetyName(target.Name)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
            var safetyName = NormalizeQuickSnapshotName(safetyBase);

            var existingNames = points
                .Select(item => item.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var suffix = 2;
            while (existingNames.Contains(safetyName))
            {
                safetyName = NormalizeQuickSnapshotName($"{safetyBase} {suffix++}");
            }

            var safety = await powerShell.CreateVmCheckpointAsync(
                vmName,
                safetyName,
                settings.PreferProduction);

            var now = DateTime.UtcNow;
            var safetyExpiry = now.AddHours(settings.SafetyRetentionHours);
            await db.UpsertQuickSnapshotMetadataAsync(new VmQuickSnapshotMetadata(
                vmName,
                safety.Id,
                safety.Name,
                $"Automatic safety point created before restoring '{target.Name}'.",
                false,
                true,
                safetyExpiry,
                currentUser!.Id,
                currentUser.Username,
                now,
                now));

            try
            {
                if (wasRunning)
                {
                    await availability.MarkPlannedDowntimeAsync(vmName, $"Quick Restore Point restore by {currentUser!.Username}", TimeSpan.FromHours(2));
                    await powerShell.StopVmForCheckpointRestoreAsync(vmName);
                }

                await powerShell.RestoreVmCheckpointByIdAsync(vmName, target.Id);

                var afterRestore = await powerShell.GetVmStateAsync(vmName);
                if (IsRunningLikeState(afterRestore))
                {
                    await powerShell.StopVmForCheckpointRestoreAsync(vmName);
                }

                if (wasRunning)
                {
                    await powerShell.StartVmAsync(vmName);
                }

                var finalState = await powerShell.GetVmStateAsync(vmName);

                await db.AddAuditAsync(
                    "VM_QUICK_RESTORE_POINT_RESTORED",
                    "vm",
                    vmName,
                    $"Restore Point '{target.Name}' restored by {currentUser.Username}. Safety point '{safety.Name}' retained until approximately {safetyExpiry.ToLocalTime():yyyy-MM-dd HH:mm}. Previous state={previousState}; final state={finalState}.");

                return new QuickRestorePointRestoreResult(
                    vmName,
                    target.Name,
                    safety.Name,
                    wasRunning,
                    previousState,
                    finalState,
                    $"Quick Restore Point '{target.Name}' restored. Safety point '{safety.Name}' was kept temporarily. {(wasRunning ? "The VM was started again." : "The VM remains stopped.")}");
            }
            catch (Exception ex)
            {
                await db.AddAuditAsync(
                    "VM_QUICK_RESTORE_POINT_RESTORE_FAILED",
                    "vm",
                    vmName,
                    $"Restore of '{target.Name}' failed after creating safety point '{safety.Name}': {ex.Message}");
                throw new InvalidOperationException(
                    $"Restore failed, but safety point '{safety.Name}' was created first and has been kept. {ex.Message}",
                    ex);
            }
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    public async Task DeleteQuickRestorePointAsync(
        PanelUser? currentUser,
        string vmName,
        string snapshotId)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");

        await _provisioningLock.WaitAsync();
        try
        {
            var points = await GetQuickRestorePointsAsync(currentUser, vmName);
            var point = points.FirstOrDefault(item =>
                string.Equals(item.Id, snapshotId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException("Quick Restore Point no longer exists.");

            if (point.IsPinned)
            {
                throw new InvalidOperationException(
                    $"Quick Restore Point '{point.Name}' is the permanent restore point. Remove permanent protection before deleting it.");
            }

            await powerShell.DeleteVmCheckpointByIdAsync(vmName, snapshotId);
            await db.DeleteQuickSnapshotMetadataAsync(vmName, snapshotId);

            await db.AddAuditAsync(
                "VM_QUICK_RESTORE_POINT_DELETED",
                "vm",
                vmName,
                $"Quick Restore Point '{point.Name}' deleted by {currentUser!.Username}.");
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    public async Task<int> CleanupExpiredQuickRestorePointsAsync(
        PanelUser? currentUser,
        string vmName)
    {
        await EnsureCanAccessVmAsync(currentUser, vmName);
        RequireUserPermission(currentUser, currentUser!.EffectivePermissions.CanViewBackups, "Backups are not enabled for your account.");

        await _provisioningLock.WaitAsync();
        try
        {
            var points = await GetQuickRestorePointsAsync(currentUser, vmName);
            var expired = points
                .Where(item => item.IsExpired && !item.IsPinned)
                .OrderBy(item => item.CreationTime)
                .ToList();

            var removed = 0;
            foreach (var point in expired)
            {
                await powerShell.DeleteVmCheckpointByIdAsync(vmName, point.Id);
                await db.DeleteQuickSnapshotMetadataAsync(vmName, point.Id);
                removed++;

                await db.AddAuditAsync(
                    "VM_QUICK_RESTORE_POINT_EXPIRED_CLEANED",
                    "vm",
                    vmName,
                    $"Expired Quick Restore Point '{point.Name}' removed by {currentUser!.Username}.");
            }

            return removed;
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    // Compatibility wrappers retained for older UI/API call sites.
    public async Task CreateCheckpointAsync(PanelUser? currentUser, string vmName, string checkpointName)
    {
        _ = await CreateQuickRestorePointAsync(currentUser, vmName, checkpointName, string.Empty);
    }

    public async Task<CheckpointRestoreResult> RestoreCheckpointAsync(
        PanelUser? currentUser,
        string vmName,
        string checkpointName)
    {
        var point = (await GetQuickRestorePointsAsync(currentUser, vmName))
            .FirstOrDefault(item => string.Equals(item.Name, checkpointName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("Quick Restore Point no longer exists.");

        var result = await RestoreQuickRestorePointAsync(currentUser, vmName, point.Id);
        return new CheckpointRestoreResult(
            result.VmName,
            result.RestorePointName,
            result.WasRunning,
            result.PreviousState,
            result.FinalState,
            result.Message);
    }

    public async Task DeleteCheckpointAsync(
        PanelUser? currentUser,
        string vmName,
        string checkpointName)
    {
        var point = (await GetQuickRestorePointsAsync(currentUser, vmName))
            .FirstOrDefault(item => string.Equals(item.Name, checkpointName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("Quick Restore Point no longer exists.");

        await DeleteQuickRestorePointAsync(currentUser, vmName, point.Id);
    }

    private QuickSnapshotSettings ReadQuickSnapshotSettings()
    {
        return new QuickSnapshotSettings(
            Math.Clamp(configuration.GetValue("QuickSnapshots:MaxPerVm", 3), 1, 20),
            Math.Clamp(configuration.GetValue("QuickSnapshots:MaxAgeDays", 7), 1, 365),
            Math.Clamp(configuration.GetValue("QuickSnapshots:SafetyRetentionHours", 24), 1, 168),
            Math.Clamp(configuration.GetValue("QuickSnapshots:WarningStorageGb", 20), 1, 10000) * 1024L * 1024L * 1024L,
            configuration.GetValue("QuickSnapshots:PreferProduction", true));
    }

    private static string NormalizeQuickSnapshotName(string value)
    {
        var name = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Restore Point name cannot be empty.");
        }

        return name.Length > 100 ? name[..100].Trim() : name;
    }

    private static string NormalizeQuickSnapshotDescription(string value)
    {
        var description = (value ?? string.Empty).Trim();
        return description.Length > 500 ? description[..500].Trim() : description;
    }

    private static string SanitizeQuickSnapshotSafetyName(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length > 42) text = text[..42].Trim();
        return string.IsNullOrWhiteSpace(text) ? "restore point" : text;
    }

    private sealed record QuickSnapshotSettings(
        int MaxPerVm,
        int MaxAgeDays,
        int SafetyRetentionHours,
        long WarningStorageBytes,
        bool PreferProduction);

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

        if (config.AutoDisconnect || config.AutoSuspend)
        {
            try
            {
                await selfHealing.SetSafetyHoldAsync(
                    vmName,
                    "Security hold: confirmed outbound abuse mitigation",
                    TimeSpan.FromDays(3650));
                actions.Add("self-healing security hold set");
            }
            catch (Exception ex)
            {
                actions.Add($"self-healing security hold failed: {ex.Message}");
            }
        }

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
        var rdpStart = Math.Clamp(configuration.GetValue("Provisioning:RdpPortStart", 22000), 1024, 65535);
        var rdpEnd = Math.Clamp(configuration.GetValue("Provisioning:RdpPortEnd", 22999), rdpStart, 65535);
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
            if (requestedPort.Value is < 1024 or > 65535)
            {
                throw new InvalidOperationException("RDP external port must be between 1024 and 65535.");
            }

            // An explicit manual port may be outside the automatic allocation
            // pool. It is still accepted when it is not already mapped, listened
            // on by the host, or reserved in configuration.
            if (used.Contains(requestedPort.Value))
            {
                throw new InvalidOperationException($"RDP external port {requestedPort.Value} is already in use.");
            }

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
            yield return new PanelPortMapping("UDP", profile.RdpExternalPort.Value, profile.RdpInternalPort);
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

    private static IEnumerable<ConsistencyExpectedMapping> BuildConsistencyExpectedMappings(VmNetworkPortProfile profile)
    {
        if (profile.EnableRdp && profile.RdpExternalPort is > 0 && profile.RdpInternalPort > 0)
        {
            yield return new ConsistencyExpectedMapping(
                profile.VmName,
                "RDP",
                "TCP",
                profile.RdpExternalPort.Value,
                profile.InternalIpAddress,
                profile.RdpInternalPort);
            yield return new ConsistencyExpectedMapping(
                profile.VmName,
                "RDP",
                "UDP",
                profile.RdpExternalPort.Value,
                profile.InternalIpAddress,
                profile.RdpInternalPort);
        }

        if (profile.EnableFiveM && profile.FiveMExternalPort is > 0 && profile.FiveMInternalPort > 0)
        {
            yield return new ConsistencyExpectedMapping(
                profile.VmName,
                "FiveM",
                "TCP",
                profile.FiveMExternalPort.Value,
                profile.InternalIpAddress,
                profile.FiveMInternalPort);
            yield return new ConsistencyExpectedMapping(
                profile.VmName,
                "FiveM",
                "UDP",
                profile.FiveMExternalPort.Value,
                profile.InternalIpAddress,
                profile.FiveMInternalPort);
        }

        foreach (var item in ParseExtraPorts(profile.ExtraTcpPorts, "TCP"))
        {
            yield return new ConsistencyExpectedMapping(
                profile.VmName,
                "Custom",
                item.Protocol,
                item.ExternalPort,
                profile.InternalIpAddress,
                item.InternalPort);
        }

        foreach (var item in ParseExtraPorts(profile.ExtraUdpPorts, "UDP"))
        {
            yield return new ConsistencyExpectedMapping(
                profile.VmName,
                "Custom",
                item.Protocol,
                item.ExternalPort,
                profile.InternalIpAddress,
                item.InternalPort);
        }
    }

    private static int CleanupKindSort(string kind) => kind.ToLowerInvariant() switch
    {
        "orphan-nat" => 0,
        "orphan-firewall" => 1,
        "stale-panel-state" => 2,
        "stale-assignment" => 3,
        "stale-recovery" => 4,
        _ => 10
    };

    private static int ConsistencySeveritySort(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => 0,
        "warning" => 1,
        "review" => 2,
        _ => 3
    };

    private sealed record ConsistencyExpectedMapping(
        string VmName,
        string Label,
        string Protocol,
        int ExternalPort,
        string InternalIpAddress,
        int InternalPort);

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
        var publicIp =
            string.IsNullOrWhiteSpace(profile.PublicIpAddress)
                ? access.IpAddress
                : profile.PublicIpAddress.Trim();
        var profileOrExistingHost =
            string.IsNullOrWhiteSpace(profile.PublicHostname)
                ? access.Hostname
                : profile.PublicHostname.Trim();
        var publicHost = GetRdpCustomerHost(
            publicIp,
            profileOrExistingHost);

        var updated = access with
        {
            Hostname = publicHost,
            IpAddress = publicIp,
            RdpPort = profile.RdpExternalPort,
            UpdatedAt = DateTime.UtcNow
        };
        await db.SaveVmAccessInfoAsync(updated);
    }

    private VmNetworkPortProfile NormalizeNetworkProfile(VmNetworkPortProfile profile)
    {
        var defaultFiveMInternal = Math.Clamp(configuration.GetValue("Provisioning:DefaultFiveMInternalPort", 30120), 1, 65535);

        var fiveMInternalPort = NormalizePort(profile.FiveMInternalPort, defaultFiveMInternal);

        return profile with
        {
            NatName = NormalizeNatName(profile.NatName),
            InternalIpAddress = (profile.InternalIpAddress ?? string.Empty).Trim(),
            PublicIpAddress = (profile.PublicIpAddress ?? string.Empty).Trim(),
            PublicHostname = (profile.PublicHostname ?? string.Empty).Trim(),
            RdpInternalPort = NormalizePort(profile.RdpInternalPort, 3389),
            RdpExternalPort = NormalizeNullablePort(profile.RdpExternalPort),
            FiveMInternalPort = fiveMInternalPort,
            FiveMExternalPort = NormalizeNullablePort(profile.FiveMExternalPort),
            ExtraTcpPorts = profile.ExtraTcpPorts ?? string.Empty,
            ExtraUdpPorts = profile.ExtraUdpPorts ?? string.Empty,
            DnsInterfaceAlias = (profile.DnsInterfaceAlias ?? string.Empty).Trim(),
            PreferredDnsServer = NormalizeIpAddressOrEmpty(profile.PreferredDnsServer, "Preferred DNS"),
            AlternateDnsServer = NormalizeIpAddressOrEmpty(profile.AlternateDnsServer, "Alternate DNS"),
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static int ResolveProvisioningFiveMInternalPort(int requestedPort, int? allocatedExternalPort, VmProvisioningDefaults defaults)
    {
        // A deliberate zero means "mirror public". Resolve it only after the
        // collision-safe external port has been allocated.
        if (requestedPort == 0 && allocatedExternalPort is > 0)
        {
            return allocatedExternalPort.Value;
        }

        return requestedPort is >= 1 and <= 65535
            ? requestedPort
            : defaults.DefaultFiveMInternalPort;
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

    private async Task MarkPowerActionAvailabilityWindowAsync(string vmName, string action, string actor, bool internalAction)
    {
        switch (action)
        {
            case "stop":
                await availability.MarkPlannedDowntimeAsync(vmName, $"Planned stop by {actor}", TimeSpan.FromMinutes(internalAction ? 120 : 10));
                break;
            case "restart":
                await availability.MarkPlannedDowntimeAsync(vmName, $"Planned restart by {actor}", TimeSpan.FromMinutes(15));
                break;
            case "suspend":
                await availability.MarkPlannedDowntimeAsync(vmName, $"Planned suspension by {actor}", TimeSpan.FromDays(internalAction ? 30 : 1));
                break;
            case "start":
            case "resume":
                await availability.ClearPlannedDowntimeAsync(vmName, actor);
                break;
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

    private async Task<bool> SendAgentlessEmergencyPasswordEmailAsync(
        PanelUser currentUser,
        string vmName,
        string username,
        string newPassword)
    {
        var to = await db.GetUserEmailAsync(currentUser.Id);
        if (string.IsNullOrWhiteSpace(to))
        {
            return false;
        }

        var body = $"Hello {currentUser.Username},\n\nYour emergency Windows/RDP password reset for {vmName} completed successfully through PenguinPanel's agentless Hyper-V recovery.\n\nVM: {vmName}\nWindows username: {username}\nNew password: {newPassword}\n\nNo Guest Agent was required. For security, change this password again after you regain access if you believe the previous credentials were compromised.";

        return await TrySendEmailAsync(
            to,
            $"PenguinPanel emergency password - {vmName}",
            body,
            BuildPanelEmailHtml(
                "Emergency password",
                "Agentless recovery completed",
                $"Hello {currentUser.Username}, PenguinPanel reset the Windows/RDP password through Hyper-V PowerShell Direct.",
                [
                    new("VM", vmName),
                    new("Windows username", username),
                    new("New password", newPassword, true),
                    new("Recovery method", "Agentless Hyper-V PowerShell Direct")
                ],
                "The private PenguinPanel recovery credential was rotated automatically and is never shown to customers.",
                "Open PenguinPanel",
                PanelPublicUrl));
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

    private async Task<int> NotifyAssignedUsersReinstallCredentialsAsync(
        string vmName,
        string templateName,
        string finalState,
        string windowsUsername,
        string windowsPassword,
        string rdpHost,
        int? rdpPort,
        bool passwordWasGenerated,
        string templateNotes)
    {
        if (!email.IsEnabled)
        {
            return 0;
        }

        var assignments = await db.GetUserVmAssignmentsAsync();
        var matchingAssignments = assignments
            .Where(item =>
                string.Equals(
                    item.VmName,
                    vmName,
                    StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.UserId)
            .Select(group => group.First())
            .ToList();

        var sent = 0;
        foreach (var assignment in matchingAssignments)
        {
            var target = await db.GetUserByIdAsync(assignment.UserId);
            if (target is null)
            {
                continue;
            }

            var userEmail = await db.GetUserEmailAsync(assignment.UserId);
            if (string.IsNullOrWhiteSpace(userEmail))
            {
                continue;
            }

            var rdpAddress = string.IsNullOrWhiteSpace(rdpHost)
                ? "See Access in PenguinPanel"
                : rdpPort is > 0
                    ? $"{rdpHost}:{rdpPort.Value}"
                    : rdpHost;

            var passwordType = passwordWasGenerated
                ? "Automatically generated"
                : "Selected during reinstall";

            var plainBody =
                $"Hello {target.Username},\n\n"
                + $"Your virtual server {vmName} has been reinstalled successfully from {templateName}.\n\n"
                + $"Server: {vmName}\n"
                + $"State: {finalState}\n"
                + $"Remote Desktop: {rdpAddress}\n"
                + $"Windows username: {windowsUsername}\n"
                + $"New Windows/RDP password: {windowsPassword}\n"
                + $"Password type: {passwordType}\n\n"
                + "The same login details are also available in PenguinPanel -> Access."
                + (string.IsNullOrWhiteSpace(templateNotes)
                    ? string.Empty
                    : $"\n\nTemplate information:\n{templateNotes.Trim()}");

            var note =
                passwordWasGenerated
                    ? "You left the password fields blank, so PenguinPanel generated this fresh password automatically. Keep it somewhere safe or change it from Windows after you sign in."
                    : "This is the final Windows password selected during the reinstall. The same credential is stored in PenguinPanel Access.";

            var html = BuildPanelEmailHtml(
                "VPS reinstalled",
                "Your Windows login is ready",
                $"Hello {target.Username}, {vmName} has finished reinstalling and the Windows login is ready.",
                [
                    new("Server", vmName),
                    new("Template", templateName),
                    new("State", finalState),
                    new("Remote Desktop", rdpAddress),
                    new("Windows username", windowsUsername),
                    new("New password", windowsPassword, true),
                    new("Password", passwordType)
                ],
                string.IsNullOrWhiteSpace(templateNotes)
                    ? note
                    : note + " Template information: " + templateNotes.Trim(),
                "Open PenguinPanel",
                PanelPublicUrl);

            if (await TrySendEmailAsync(
                    userEmail,
                    $"PenguinPanel: {vmName} reinstall login details",
                    plainBody,
                    html))
            {
                sent++;
            }
        }

        return sent;
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

    private static string GenerateRecoveryPassword()
    {
        return "Pr!" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)) + "aA9!";
    }

    private static string NormalizeRecoveryAccountUsername(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return "PenguinRecovery";
        }

        if (value.Length > 20
            || value.IndexOfAny(['\\', '/', '"', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '@']) >= 0)
        {
            return "PenguinRecovery";
        }

        return value;
    }

    private static string NormalizeLocalWindowsUsername(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Contains('\\'))
        {
            value = value.Split('\\')[^1];
        }

        return value;
    }

    private static string GenerateFriendlyPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@$%";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(18);
        var chars = bytes.Select(item => alphabet[item % alphabet.Length]).ToArray();
        return new string(chars);
    }

    private string GetRdpCustomerHost(
        string fallbackAddress,
        string? fallbackHostname = null)
    {
        var configured =
            (configuration["Provisioning:RdpCustomerHostname"] ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(configured))
        {
            configured =
                (configuration["Provisioning:PublicHostname"] ?? string.Empty).Trim();
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = (fallbackHostname ?? string.Empty).Trim();
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            return fallbackAddress;
        }

        if (Uri.TryCreate(
                configured,
                UriKind.Absolute,
                out var absoluteUri)
            && !string.IsNullOrWhiteSpace(absoluteUri.Host))
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

        return string.IsNullOrWhiteSpace(configured)
            ? fallbackAddress
            : configured;
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
