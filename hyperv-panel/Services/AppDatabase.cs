using MySqlConnector;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HyperVPanel.Services;

public sealed class AppDatabase(IConfiguration configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("PanelDb")
        ?? "Server=127.0.0.1;Port=3306;Database=hyperv_panel;User ID=hyperv_panel;Password=change-this-password;SslMode=Preferred;Allow User Variables=True;";
    private readonly SecretProtector _secrets = new(configuration);

    public async Task InitializeAsync()
    {
        await using var db = Open();
        await ExecuteAsync(db, """
            CREATE TABLE IF NOT EXISTS users (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                username VARCHAR(191) NOT NULL,
                password_hash VARCHAR(255) NOT NULL,
                role VARCHAR(32) NOT NULL DEFAULT 'admin',
                email VARCHAR(320) NOT NULL DEFAULT '',
                parent_user_id BIGINT NULL,
                password_must_change TINYINT(1) NOT NULL DEFAULT 0,
                can_view_console TINYINT(1) NOT NULL DEFAULT 1,
                can_view_resources TINYINT(1) NOT NULL DEFAULT 1,
                can_view_access TINYINT(1) NOT NULL DEFAULT 1,
                can_view_network TINYINT(1) NOT NULL DEFAULT 0,
                can_view_backups TINYINT(1) NOT NULL DEFAULT 0,
                can_request_reinstall TINYINT(1) NOT NULL DEFAULT 1,
                can_view_activity TINYINT(1) NOT NULL DEFAULT 1,
                can_view_settings TINYINT(1) NOT NULL DEFAULT 0,
                can_use_power_actions TINYINT(1) NOT NULL DEFAULT 1,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                UNIQUE KEY ux_users_username (username),
                KEY ix_users_parent_user_id (parent_user_id),
                CONSTRAINT fk_users_parent_user_id FOREIGN KEY(parent_user_id) REFERENCES users(id) ON DELETE CASCADE
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS user_vm_assignments (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                user_id BIGINT NOT NULL,
                vm_name VARCHAR(191) NOT NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                UNIQUE KEY ux_user_vm_assignments_user_vm (user_id, vm_name),
                CONSTRAINT fk_user_vm_assignments_user_id FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS user_sessions (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                user_id BIGINT NOT NULL,
                token_hash VARCHAR(128) NOT NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                expires_at DATETIME(6) NOT NULL,
                last_seen_at DATETIME(6) NULL,
                UNIQUE KEY ux_user_sessions_token_hash (token_hash),
                CONSTRAINT fk_user_sessions_user_id FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS login_attempts (
                username VARCHAR(191) NOT NULL PRIMARY KEY,
                failed_count INT NOT NULL DEFAULT 0,
                locked_until DATETIME(6) NULL,
                last_failed_at DATETIME(6) NULL
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS user_resource_limits (
                user_id BIGINT NOT NULL PRIMARY KEY,
                max_vm_count INT NOT NULL DEFAULT 0,
                max_cpu_cores INT NOT NULL DEFAULT 0,
                max_memory_mb INT NOT NULL DEFAULT 0,
                max_disk_gb INT NOT NULL DEFAULT 0,
                max_power_actions_per_hour INT NOT NULL DEFAULT 20,
                updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                CONSTRAINT fk_user_resource_limits_user_id FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS user_power_actions (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                user_id BIGINT NOT NULL,
                vm_name VARCHAR(191) NOT NULL,
                action VARCHAR(32) NOT NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                KEY ix_user_power_actions_user_created (user_id, created_at),
                CONSTRAINT fk_user_power_actions_user_id FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS audit_logs (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                action VARCHAR(96) NOT NULL,
                resource_type VARCHAR(64) NOT NULL,
                resource_id VARCHAR(191) NULL,
                details TEXT NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS bandwidth_history (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                vm_name VARCHAR(191) NOT NULL,
                bytes_in BIGINT NOT NULL,
                bytes_out BIGINT NOT NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                KEY ix_bandwidth_history_vm_created (vm_name, created_at)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS abuse_events (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                vm_name VARCHAR(191) NOT NULL,
                detection_type VARCHAR(64) NOT NULL,
                severity VARCHAR(32) NOT NULL,
                outbound_mbps DOUBLE NOT NULL,
                action_taken VARCHAR(255) NOT NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_access_info (
                vm_name VARCHAR(191) NOT NULL PRIMARY KEY,
                hostname VARCHAR(255) NOT NULL DEFAULT '',
                ip_address VARCHAR(64) NOT NULL DEFAULT '',
                rdp_port INT NULL,
                ssh_port INT NULL,
                username VARCHAR(191) NOT NULL DEFAULT '',
                password_secret TEXT NULL,
                notes TEXT NULL,
                web_console_url VARCHAR(2048) NOT NULL DEFAULT '',
                updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_network_profiles (
                vm_name VARCHAR(191) NOT NULL PRIMARY KEY,
                nat_name VARCHAR(191) NOT NULL DEFAULT '',
                internal_ip_address VARCHAR(64) NOT NULL DEFAULT '',
                public_ip_address VARCHAR(64) NOT NULL DEFAULT '',
                public_hostname VARCHAR(255) NOT NULL DEFAULT '',
                enable_rdp TINYINT(1) NOT NULL DEFAULT 1,
                rdp_external_port INT NULL,
                rdp_internal_port INT NOT NULL DEFAULT 3389,
                enable_fivem TINYINT(1) NOT NULL DEFAULT 0,
                fivem_external_port INT NULL,
                fivem_internal_port INT NOT NULL DEFAULT 30120,
                extra_tcp_ports TEXT NULL,
                extra_udp_ports TEXT NULL,
                dns_interface_alias VARCHAR(191) NOT NULL DEFAULT '',
                preferred_dns_server VARCHAR(64) NOT NULL DEFAULT '',
                alternate_dns_server VARCHAR(64) NOT NULL DEFAULT '',
                updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_public_status (
                vm_name VARCHAR(191) NOT NULL PRIMARY KEY,
                is_public TINYINT(1) NOT NULL DEFAULT 0,
                display_name VARCHAR(191) NOT NULL DEFAULT '',
                public_note TEXT NULL,
                port_check_port INT NULL,
                updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_guest_agents (
                vm_name VARCHAR(191) NOT NULL PRIMARY KEY,
                is_enabled TINYINT(1) NOT NULL DEFAULT 0,
                token_hash VARCHAR(128) NULL,
                last_seen_at DATETIME(6) NULL,
                last_agent_version VARCHAR(64) NOT NULL DEFAULT '',
                last_ip_address VARCHAR(64) NOT NULL DEFAULT '',
                updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                UNIQUE KEY ux_vm_guest_agents_token_hash (token_hash)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_recovery_accounts (
                vm_name VARCHAR(191) NOT NULL PRIMARY KEY,
                is_enabled TINYINT(1) NOT NULL DEFAULT 0,
                username VARCHAR(191) NOT NULL DEFAULT '',
                password_secret TEXT NULL,
                last_rotated_at DATETIME(6) NULL,
                last_used_at DATETIME(6) NULL,
                last_error TEXT NULL,
                updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS guest_agent_commands (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                vm_name VARCHAR(191) NOT NULL,
                command_type VARCHAR(64) NOT NULL,
                status VARCHAR(32) NOT NULL DEFAULT 'pending',
                payload_secret TEXT NULL,
                result_message TEXT NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                started_at DATETIME(6) NULL,
                completed_at DATETIME(6) NULL,
                KEY ix_guest_agent_commands_vm_status (vm_name, status, created_at)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_templates (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                name VARCHAR(191) NOT NULL,
                operating_system VARCHAR(64) NOT NULL DEFAULT '',
                iso_path VARCHAR(2048) NOT NULL DEFAULT '',
                notes TEXT NULL,
                is_enabled TINYINT(1) NOT NULL DEFAULT 1,
                bootstrap_username VARCHAR(191) NOT NULL DEFAULT 'Administrator',
                bootstrap_password_secret TEXT NULL,
                vm_generation INT NOT NULL DEFAULT 2,
                validation_status VARCHAR(32) NOT NULL DEFAULT 'unverified',
                validation_message TEXT NULL,
                validation_windows_product VARCHAR(191) NOT NULL DEFAULT '',
                validation_windows_version VARCHAR(64) NOT NULL DEFAULT '',
                validation_build VARCHAR(64) NOT NULL DEFAULT '',
                validation_architecture VARCHAR(64) NOT NULL DEFAULT '',
                validation_powershell_direct TINYINT(1) NOT NULL DEFAULT 0,
                validation_rdp_service TINYINT(1) NOT NULL DEFAULT 0,
                validation_rdp_enabled TINYINT(1) NOT NULL DEFAULT 0,
                validation_rdp_status VARCHAR(64) NOT NULL DEFAULT '',
                validation_virtual_disk_bytes BIGINT NOT NULL DEFAULT 0,
                validation_physical_disk_bytes BIGINT NOT NULL DEFAULT 0,
                validation_file_last_write_ticks BIGINT NOT NULL DEFAULT 0,
                validation_checked_at DATETIME(6) NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                UNIQUE KEY ux_vm_templates_name (name)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS whmcs_services (
                service_id BIGINT NOT NULL PRIMARY KEY,
                client_id BIGINT NOT NULL,
                panel_user_id BIGINT NOT NULL,
                vm_name VARCHAR(191) NOT NULL,
                status VARCHAR(32) NOT NULL DEFAULT 'provisioning',
                last_error TEXT NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                UNIQUE KEY ux_whmcs_services_vm_name (vm_name),
                KEY ix_whmcs_services_client (client_id, updated_at),
                KEY ix_whmcs_services_panel_user (panel_user_id, updated_at)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS whmcs_managed_users (
                panel_user_id BIGINT NOT NULL PRIMARY KEY,
                client_id BIGINT NOT NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                KEY ix_whmcs_managed_users_client (client_id)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS reinstall_requests (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                user_id BIGINT NOT NULL,
                vm_name VARCHAR(191) NOT NULL,
                template_id BIGINT NOT NULL,
                status VARCHAR(32) NOT NULL DEFAULT 'pending',
                notes TEXT NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                CONSTRAINT fk_reinstall_requests_user_id FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE,
                CONSTRAINT fk_reinstall_requests_template_id FOREIGN KEY(template_id) REFERENCES vm_templates(id) ON DELETE CASCADE
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_quick_snapshot_metadata (
                vm_name VARCHAR(191) NOT NULL,
                snapshot_id VARCHAR(191) NOT NULL,
                snapshot_name VARCHAR(191) NOT NULL DEFAULT '',
                description TEXT NULL,
                is_pinned TINYINT(1) NOT NULL DEFAULT 0,
                is_temporary_safety TINYINT(1) NOT NULL DEFAULT 0,
                expires_at DATETIME(6) NULL,
                created_by_user_id BIGINT NULL,
                created_by_username VARCHAR(191) NOT NULL DEFAULT '',
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                PRIMARY KEY (vm_name, snapshot_id),
                KEY ix_vm_quick_snapshot_meta_expiry (vm_name, expires_at),
                KEY ix_vm_quick_snapshot_meta_pinned (vm_name, is_pinned)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_reinstall_rollbacks (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                vm_name VARCHAR(191) NOT NULL,
                template_name VARCHAR(191) NOT NULL DEFAULT '',
                rollback_disk_path VARCHAR(2048) NOT NULL DEFAULT '',
                reinstalled_disk_path VARCHAR(2048) NOT NULL DEFAULT '',
                virtual_disk_bytes BIGINT NOT NULL DEFAULT 0,
                physical_disk_bytes BIGINT NOT NULL DEFAULT 0,
                status VARCHAR(32) NOT NULL DEFAULT 'available',
                was_running TINYINT(1) NOT NULL DEFAULT 0,
                previous_state VARCHAR(64) NOT NULL DEFAULT '',
                old_hostname VARCHAR(255) NOT NULL DEFAULT '',
                old_ip_address VARCHAR(64) NOT NULL DEFAULT '',
                old_rdp_port INT NULL,
                old_ssh_port INT NULL,
                old_username VARCHAR(191) NOT NULL DEFAULT '',
                old_password_secret TEXT NULL,
                old_notes TEXT NULL,
                old_web_console_url VARCHAR(2048) NOT NULL DEFAULT '',
                created_by_user_id BIGINT NULL,
                created_by_username VARCHAR(191) NOT NULL DEFAULT '',
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                expires_at DATETIME(6) NOT NULL,
                restored_at DATETIME(6) NULL,
                deleted_at DATETIME(6) NULL,
                last_error TEXT NULL,
                KEY ix_vm_reinstall_rollbacks_vm_status (vm_name, status, created_at),
                KEY ix_vm_reinstall_rollbacks_expiry (status, expires_at)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_backups (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                vm_name VARCHAR(191) NOT NULL,
                name VARCHAR(191) NOT NULL,
                backup_type VARCHAR(32) NOT NULL DEFAULT 'manual',
                status VARCHAR(32) NOT NULL DEFAULT 'creating',
                backup_path VARCHAR(2048) NOT NULL DEFAULT '',
                manifest_path VARCHAR(2048) NOT NULL DEFAULT '',
                total_bytes BIGINT NOT NULL DEFAULT 0,
                disk_count INT NOT NULL DEFAULT 0,
                was_running TINYINT(1) NOT NULL DEFAULT 0,
                created_by_user_id BIGINT NULL,
                created_by_username VARCHAR(191) NOT NULL DEFAULT '',
                last_error TEXT NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                completed_at DATETIME(6) NULL,
                KEY ix_vm_backups_vm_created (vm_name, created_at),
                KEY ix_vm_backups_vm_status (vm_name, status)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS monitoring_alerts (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                alert_key VARCHAR(255) NOT NULL,
                scope_type VARCHAR(32) NOT NULL,
                scope_name VARCHAR(191) NOT NULL DEFAULT '',
                alert_type VARCHAR(64) NOT NULL,
                severity VARCHAR(32) NOT NULL,
                title VARCHAR(255) NOT NULL,
                message TEXT NOT NULL,
                current_value DOUBLE NULL,
                threshold_value DOUBLE NULL,
                status VARCHAR(32) NOT NULL DEFAULT 'active',
                occurrence_count INT NOT NULL DEFAULT 1,
                first_triggered_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                last_triggered_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                last_notified_at DATETIME(6) NULL,
                acknowledged_at DATETIME(6) NULL,
                acknowledged_by VARCHAR(191) NOT NULL DEFAULT '',
                resolved_at DATETIME(6) NULL,
                resolution_message TEXT NULL,
                KEY ix_monitoring_alerts_status_time (status, last_triggered_at),
                KEY ix_monitoring_alerts_key_status (alert_key, status)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_availability_state (
                vm_name VARCHAR(191) NOT NULL PRIMARY KEY,
                last_state VARCHAR(64) NOT NULL DEFAULT 'Unknown',
                last_heartbeat_healthy TINYINT(1) NOT NULL DEFAULT 0,
                was_observed_running TINYINT(1) NOT NULL DEFAULT 0,
                down_was_planned TINYINT(1) NOT NULL DEFAULT 0,
                last_sample_at DATETIME(6) NOT NULL,
                down_since DATETIME(6) NULL,
                unresponsive_since DATETIME(6) NULL,
                planned_until DATETIME(6) NULL,
                planned_reason VARCHAR(255) NOT NULL DEFAULT '',
                unexpected_notified_at DATETIME(6) NULL,
                extended_notified_at DATETIME(6) NULL,
                unresponsive_notified_at DATETIME(6) NULL,
                updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_availability_events (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                vm_name VARCHAR(191) NOT NULL,
                event_type VARCHAR(64) NOT NULL,
                severity VARCHAR(32) NOT NULL,
                title VARCHAR(255) NOT NULL,
                message TEXT NOT NULL,
                started_at DATETIME(6) NOT NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                KEY ix_vm_availability_events_vm_created (vm_name, created_at),
                KEY ix_vm_availability_events_type_created (event_type, created_at)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_availability_preferences (
                user_id BIGINT NOT NULL,
                vm_name VARCHAR(191) NOT NULL,
                is_enabled TINYINT(1) NOT NULL DEFAULT 1,
                notify_unexpected_down TINYINT(1) NOT NULL DEFAULT 1,
                notify_extended_downtime TINYINT(1) NOT NULL DEFAULT 1,
                notify_guest_unresponsive TINYINT(1) NOT NULL DEFAULT 1,
                notify_recovery TINYINT(1) NOT NULL DEFAULT 1,
                updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                PRIMARY KEY (user_id, vm_name),
                CONSTRAINT fk_vm_availability_preferences_user_id FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_self_healing_preferences (
                vm_name VARCHAR(191) NOT NULL PRIMARY KEY,
                is_enabled TINYINT(1) NOT NULL DEFAULT 0,
                recover_unexpected_stop TINYINT(1) NOT NULL DEFAULT 1,
                recover_unresponsive TINYINT(1) NOT NULL DEFAULT 0,
                hold_until DATETIME(6) NULL,
                hold_reason VARCHAR(255) NOT NULL DEFAULT '',
                updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_self_healing_state (
                vm_name VARCHAR(191) NOT NULL PRIMARY KEY,
                incident_key VARCHAR(255) NOT NULL DEFAULT '',
                incident_type VARCHAR(64) NOT NULL DEFAULT '',
                incident_started_at DATETIME(6) NULL,
                attempt_count INT NOT NULL DEFAULT 0,
                window_started_at DATETIME(6) NULL,
                last_attempt_at DATETIME(6) NULL,
                next_attempt_at DATETIME(6) NULL,
                status VARCHAR(64) NOT NULL DEFAULT 'idle',
                last_result TEXT NULL,
                updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_self_healing_events (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                vm_name VARCHAR(191) NOT NULL,
                incident_type VARCHAR(64) NOT NULL,
                event_type VARCHAR(64) NOT NULL,
                status VARCHAR(32) NOT NULL,
                attempt_number INT NOT NULL DEFAULT 0,
                message TEXT NOT NULL,
                created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                KEY ix_vm_self_healing_events_vm_created (vm_name, created_at),
                KEY ix_vm_self_healing_events_type_created (event_type, created_at)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS host_performance_history (
                sampled_at DATETIME NOT NULL PRIMARY KEY,
                cpu_percent DOUBLE NOT NULL,
                memory_percent DOUBLE NOT NULL,
                disk_free_percent DOUBLE NOT NULL,
                running_vm_count INT NOT NULL,
                total_vm_count INT NOT NULL,
                KEY ix_host_performance_history_sampled (sampled_at)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS vm_performance_history (
                vm_name VARCHAR(191) NOT NULL,
                sampled_at DATETIME NOT NULL,
                cpu_percent DOUBLE NOT NULL,
                memory_pressure_percent DOUBLE NOT NULL,
                network_receive_bps DOUBLE NOT NULL,
                network_send_bps DOUBLE NOT NULL,
                disk_read_bps DOUBLE NOT NULL,
                disk_write_bps DOUBLE NOT NULL,
                is_running TINYINT(1) NOT NULL,
                PRIMARY KEY (vm_name, sampled_at),
                KEY ix_vm_performance_history_sampled (sampled_at)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            CREATE TABLE IF NOT EXISTS settings (
                `key` VARCHAR(191) NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            """);

        await EnsureUsersSchemaAsync(db);
        await EnsureVmAccessInfoSchemaAsync(db);
        await EnsureVmNetworkProfileSchemaAsync(db);
        await EnsureVmTemplateSchemaAsync(db);
        await EnsureVmAvailabilitySchemaAsync(db);
        await EnsureDefaultTemplatesAsync(db);
        await ExecuteAsync(db, "UPDATE users SET role = 'user' WHERE role IS NULL OR trim(role) = '' OR lower(role) NOT IN ('admin', 'user')");
        await ExecuteAsync(db, "UPDATE users SET role = 'admin' WHERE lower(username) = 'admin'");

        await EnsureSettingAsync("abuse_enabled", "1");
        await EnsureSettingAsync("abuse_auto_disconnect", "0");
        await EnsureSettingAsync("abuse_auto_suspend", "0");
        await EnsureSettingAsync("abuse_outbound_threshold_mbps", "50");
        await EnsureSettingAsync("abuse_sustained_threshold_mbps", "100");
        await EnsureSettingAsync("abuse_auto_scan_enabled", "0");
        await EnsureSettingAsync("abuse_scan_interval_seconds", "30");
        await EnsureSettingAsync("abuse_cooldown_minutes", "10");
        await EnsureSettingAsync("abuse_required_consecutive_detections", "3");
        await EnsureSettingAsync("monitoring_alerts_enabled", "1");
        await EnsureSettingAsync("monitoring_alerts_email_enabled", "1");
        await EnsureSettingAsync("monitoring_alerts_recovery_emails", "1");
        await EnsureSettingAsync("monitoring_alerts_required_samples", "3");
        await EnsureSettingAsync("monitoring_alerts_reminder_minutes", "30");
        await EnsureSettingAsync("monitoring_alerts_host_cpu_percent", "90");
        await EnsureSettingAsync("monitoring_alerts_host_memory_percent", "90");
        await EnsureSettingAsync("monitoring_alerts_host_disk_free_percent", "10");
        await EnsureSettingAsync("monitoring_alerts_vm_cpu_percent", "90");
        await EnsureSettingAsync("monitoring_alerts_vm_memory_percent", "100");
        await EnsureSettingAsync("monitoring_alerts_heartbeat", "1");
        await EnsureSettingAsync("monitoring_alerts_adapters", "1");
        await EnsureSettingAsync("availability_enabled", "1");
        await EnsureSettingAsync("availability_client_email_enabled", "1");
        await EnsureSettingAsync("availability_notify_admins", "1");
        await EnsureSettingAsync("availability_unexpected_down_minutes", "1");
        await EnsureSettingAsync("availability_guest_unresponsive_minutes", "10");
        await EnsureSettingAsync("availability_extended_downtime_minutes", "60");
        await ApplyAvailabilityOneHourDefaultMigrationAsync();
        await EnsureSettingAsync("availability_recovery_notifications", "1");
        await EnsureSettingAsync("self_healing_enabled", "0");
        await EnsureSettingAsync("self_healing_client_email_enabled", "1");
        await EnsureSettingAsync("self_healing_notify_admins", "1");
        await EnsureSettingAsync("self_healing_unexpected_stop_recovery", "1");
        await EnsureSettingAsync("self_healing_heartbeat_recovery", "0");
        await EnsureSettingAsync("self_healing_unexpected_stop_delay_minutes", "3");
        await EnsureSettingAsync("self_healing_heartbeat_delay_minutes", "20");
        await EnsureSettingAsync("self_healing_confirmation_minutes", "3");
        await EnsureSettingAsync("self_healing_cooldown_minutes", "30");
        await EnsureSettingAsync("self_healing_max_attempts", "2");
        await EnsureSettingAsync("self_healing_attempt_window_minutes", "360");
        await EnsureSettingAsync("performance_history_enabled", "1");
        await EnsureSettingAsync("performance_history_retention_days", "30");
        await ExecuteAsync("DELETE FROM user_sessions WHERE expires_at < @now", ("@now", DateTime.UtcNow));
        await ExecuteAsync("DELETE FROM login_attempts WHERE locked_until IS NULL AND last_failed_at < @cutoff", ("@cutoff", DateTime.UtcNow.AddDays(-7)));
        await ExecuteAsync("DELETE FROM user_power_actions WHERE created_at < @cutoff", ("@cutoff", DateTime.UtcNow.AddDays(-7)));

        var adminExists = await ScalarAsync("SELECT COUNT(*) FROM users WHERE username = 'admin'") > 0;
        if (!adminExists)
        {
            var password = configuration["FirstRunAdmin:Password"] ?? "ChangeMe!123";
            await ExecuteAsync("""
                INSERT INTO users (username, password_hash, role)
                VALUES (@username, @password_hash, 'admin')
                """,
                ("@username", "admin"),
                ("@password_hash", BCrypt.Net.BCrypt.HashPassword(password)));
            await AddAuditAsync("ADMIN_CREATED", "user", "admin", "First-run admin account created");
        }

        await EnsureAdminPermissionsAsync();

        if (configuration.GetValue<bool>("AdminRecovery:Enabled"))
        {
            var recoveryPassword = configuration["AdminRecovery:Password"] ?? "ChangeMe!123";
            await ExecuteAsync("""
                UPDATE users
                SET password_hash = @password_hash, role = 'admin', password_must_change = 1
                WHERE username = 'admin'
                """,
                ("@password_hash", BCrypt.Net.BCrypt.HashPassword(recoveryPassword)));
            await ExecuteAsync("DELETE FROM user_sessions");
            await EnsureAdminPermissionsAsync();
            await AddAuditAsync("ADMIN_PASSWORD_RECOVERY", "user", "admin", "Admin password reset from appsettings AdminRecovery");
        }
    }

    private async Task EnsureAdminPermissionsAsync()
    {
        await ExecuteAsync("""
            UPDATE users
            SET can_view_console = 1,
                can_view_resources = 1,
                can_view_access = 1,
                can_view_network = 1,
                can_view_backups = 1,
                can_request_reinstall = 1,
                can_view_activity = 1,
                can_view_settings = 1,
                can_use_power_actions = 1
            WHERE lower(role) = 'admin' OR lower(username) = 'admin'
            """);
    }


    private static async Task EnsureUsersSchemaAsync(MySqlConnection db)
    {
        var columns = await GetColumnNamesAsync(db, "users");

        if (!columns.Contains("role"))
        {
            await ExecuteAsync(db, "ALTER TABLE users ADD COLUMN role VARCHAR(32) NOT NULL DEFAULT 'user'");
        }

        if (!columns.Contains("created_at"))
        {
            await ExecuteAsync(db, "ALTER TABLE users ADD COLUMN created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)");
            await ExecuteAsync(db, "UPDATE users SET created_at = CURRENT_TIMESTAMP WHERE created_at IS NULL");
        }

        if (!columns.Contains("email"))
        {
            await ExecuteAsync(db, "ALTER TABLE users ADD COLUMN email VARCHAR(320) NOT NULL DEFAULT ''");
        }

        if (!columns.Contains("parent_user_id"))
        {
            await ExecuteAsync(db, "ALTER TABLE users ADD COLUMN parent_user_id BIGINT NULL");
        }

        if (!columns.Contains("password_must_change"))
        {
            await ExecuteAsync(db, "ALTER TABLE users ADD COLUMN password_must_change TINYINT(1) NOT NULL DEFAULT 0");
        }

        var permissionColumns = new Dictionary<string, string>
        {
            ["can_view_console"] = "1",
            ["can_view_resources"] = "1",
            ["can_view_access"] = "1",
            ["can_view_network"] = "0",
            ["can_view_backups"] = "0",
            ["can_request_reinstall"] = "1",
            ["can_view_activity"] = "1",
            ["can_view_settings"] = "0",
            ["can_use_power_actions"] = "1"
        };

        foreach (var item in permissionColumns)
        {
            if (!columns.Contains(item.Key))
            {
                await ExecuteAsync(db, $"ALTER TABLE users ADD COLUMN {item.Key} TINYINT(1) NOT NULL DEFAULT {item.Value}");
            }
        }

        await ExecuteAsync(db, """
            UPDATE users
            SET can_view_console = 1,
                can_view_resources = 1,
                can_view_access = 1,
                can_view_network = 1,
                can_view_backups = 1,
                can_request_reinstall = 1,
                can_view_activity = 1,
                can_view_settings = 1,
                can_use_power_actions = 1
            WHERE lower(role) = 'admin' OR lower(username) = 'admin'
            """);
    }

    private static async Task EnsureVmAccessInfoSchemaAsync(MySqlConnection db)
    {
        var columns = await GetColumnNamesAsync(db, "vm_access_info");

        if (!columns.Contains("web_console_url"))
        {
            await ExecuteAsync(db, "ALTER TABLE vm_access_info ADD COLUMN web_console_url VARCHAR(2048) NOT NULL DEFAULT ''");
        }
    }

    private static async Task EnsureVmNetworkProfileSchemaAsync(MySqlConnection db)
    {
        var columns = await GetColumnNamesAsync(db, "vm_network_profiles");

        var schema = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nat_name"] = "VARCHAR(191) NOT NULL DEFAULT ''",
            ["internal_ip_address"] = "VARCHAR(64) NOT NULL DEFAULT ''",
            ["public_ip_address"] = "VARCHAR(64) NOT NULL DEFAULT ''",
            ["public_hostname"] = "VARCHAR(255) NOT NULL DEFAULT ''",
            ["enable_rdp"] = "TINYINT(1) NOT NULL DEFAULT 1",
            ["rdp_external_port"] = "INT NULL",
            ["rdp_internal_port"] = "INT NOT NULL DEFAULT 3389",
            ["enable_fivem"] = "TINYINT(1) NOT NULL DEFAULT 0",
            ["fivem_external_port"] = "INT NULL",
            ["fivem_internal_port"] = "INT NOT NULL DEFAULT 30120",
            ["extra_tcp_ports"] = "TEXT NULL",
            ["extra_udp_ports"] = "TEXT NULL",
            ["dns_interface_alias"] = "VARCHAR(191) NOT NULL DEFAULT ''",
            ["preferred_dns_server"] = "VARCHAR(64) NOT NULL DEFAULT ''",
            ["alternate_dns_server"] = "VARCHAR(64) NOT NULL DEFAULT ''",
            ["updated_at"] = "DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)"
        };

        foreach (var item in schema)
        {
            if (!columns.Contains(item.Key))
            {
                await ExecuteAsync(db, $"ALTER TABLE vm_network_profiles ADD COLUMN {item.Key} {item.Value}");
            }
        }
    }

    private static async Task EnsureVmTemplateSchemaAsync(MySqlConnection db)
    {
        var columns = await GetColumnNamesAsync(db, "vm_templates");

        if (!columns.Contains("bootstrap_username"))
        {
            await ExecuteAsync(
                db,
                "ALTER TABLE vm_templates ADD COLUMN bootstrap_username VARCHAR(191) NOT NULL DEFAULT 'Administrator'");
        }

        if (!columns.Contains("bootstrap_password_secret"))
        {
            await ExecuteAsync(
                db,
                "ALTER TABLE vm_templates ADD COLUMN bootstrap_password_secret TEXT NULL");
        }

        var validationSchema = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["vm_generation"] = "INT NOT NULL DEFAULT 2",
            ["validation_status"] = "VARCHAR(32) NOT NULL DEFAULT 'unverified'",
            ["validation_message"] = "TEXT NULL",
            ["validation_windows_product"] = "VARCHAR(191) NOT NULL DEFAULT ''",
            ["validation_windows_version"] = "VARCHAR(64) NOT NULL DEFAULT ''",
            ["validation_build"] = "VARCHAR(64) NOT NULL DEFAULT ''",
            ["validation_architecture"] = "VARCHAR(64) NOT NULL DEFAULT ''",
            ["validation_powershell_direct"] = "TINYINT(1) NOT NULL DEFAULT 0",
            ["validation_rdp_service"] = "TINYINT(1) NOT NULL DEFAULT 0",
            ["validation_rdp_enabled"] = "TINYINT(1) NOT NULL DEFAULT 0",
            ["validation_rdp_status"] = "VARCHAR(64) NOT NULL DEFAULT ''",
            ["validation_virtual_disk_bytes"] = "BIGINT NOT NULL DEFAULT 0",
            ["validation_physical_disk_bytes"] = "BIGINT NOT NULL DEFAULT 0",
            ["validation_file_last_write_ticks"] = "BIGINT NOT NULL DEFAULT 0",
            ["validation_checked_at"] = "DATETIME(6) NULL"
        };

        foreach (var item in validationSchema)
        {
            if (!columns.Contains(item.Key))
            {
                await ExecuteAsync(
                    db,
                    $"ALTER TABLE vm_templates ADD COLUMN {item.Key} {item.Value}");
            }
        }
    }

    private static async Task EnsureVmAvailabilitySchemaAsync(MySqlConnection db)
    {
        var columns = await GetColumnNamesAsync(db, "vm_availability_state");
        if (!columns.Contains("down_was_planned"))
        {
            await ExecuteAsync(db, "ALTER TABLE vm_availability_state ADD COLUMN down_was_planned TINYINT(1) NOT NULL DEFAULT 0 AFTER was_observed_running");
        }
    }

    private static async Task EnsureDefaultTemplatesAsync(MySqlConnection db)
    {
        var count = await ScalarAsync(db, "SELECT COUNT(*) FROM vm_templates");
        if (count > 0)
        {
            return;
        }

        await ExecuteAsync(db, """
            INSERT INTO vm_templates (name, operating_system, iso_path, notes, is_enabled) VALUES
            ('Windows Server 2022', 'Windows', '', 'Default Windows Server template placeholder. Add ISO/template path before real reinstall automation.', 1),
            ('Windows Server 2025', 'Windows', '', 'Default Windows Server template placeholder. Add ISO/template path before real reinstall automation.', 1),
            ('Ubuntu', 'Linux', '', 'Default Ubuntu template placeholder. Add ISO/template path before real reinstall automation.', 1),
            ('Debian', 'Linux', '', 'Default Debian template placeholder. Add ISO/template path before real reinstall automation.', 1)
            """);
    }

    public MySqlConnection Open()
    {
        var connection = new MySqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task<PanelUser?> ValidateLoginAsync(string username, string password)
    {
        username = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var lockedUntil = await GetLoginLockoutUntilAsync(username);
        if (lockedUntil is not null && lockedUntil.Value > DateTime.UtcNow)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((lockedUntil.Value - DateTime.UtcNow).TotalMinutes));
            throw new InvalidOperationException($"Too many failed sign-in attempts. Try again in about {minutes} minute(s).");
        }

        PanelUser? result = null;
        string? hash = null;

        await using (var db = Open())
        await using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, username, password_hash, role, parent_user_id, password_must_change,
                       can_view_console, can_view_resources, can_view_access, can_view_network,
                       can_view_backups, can_request_reinstall, can_view_activity, can_view_settings, can_use_power_actions
                FROM users
                WHERE username = @username
                """;
            cmd.Parameters.AddWithValue("@username", username);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                hash = reader.IsDBNull(2) ? null : reader.GetString(2);
                result = new PanelUser(reader.GetInt64(0), reader.GetString(1), reader.GetString(3), ReadPermissions(reader, 6), reader.IsDBNull(4) ? null : reader.GetInt64(4), ReadBool(reader, 5));
            }
        }

        var verified = false;
        if (!string.IsNullOrWhiteSpace(hash))
        {
            try
            {
                verified = BCrypt.Net.BCrypt.Verify(password, hash);
            }
            catch
            {
                verified = false;
            }
        }

        if (!verified || result is null)
        {
            await RegisterFailedLoginAsync(username);
            return null;
        }

        await ClearFailedLoginAsync(username);
        return result;
    }

    public async Task<string> CreateUserSessionAsync(long userId, TimeSpan? lifetime = null)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromDays(30));

        await ExecuteAsync("""
            INSERT INTO user_sessions (user_id, token_hash, expires_at, last_seen_at)
            VALUES (@user_id, @token_hash, @expires_at, @last_seen_at)
            """,
            ("@user_id", userId),
            ("@token_hash", HashSessionToken(token)),
            ("@expires_at", expiresAt),
            ("@last_seen_at", DateTime.UtcNow));

        return token;
    }

    public async Task<PanelUser?> ValidateUserSessionAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var tokenHash = HashSessionToken(token);
        PanelUser? user = null;
        DateTime expiresAt = DateTime.MinValue;

        await using (var db = Open())
        await using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT u.id, u.username, u.role, s.expires_at, u.parent_user_id, u.password_must_change,
                       u.can_view_console, u.can_view_resources, u.can_view_access, u.can_view_network,
                       u.can_view_backups, u.can_request_reinstall, u.can_view_activity, u.can_view_settings, u.can_use_power_actions
                FROM user_sessions s
                INNER JOIN users u ON u.id = s.user_id
                WHERE s.token_hash = @token_hash
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@token_hash", tokenHash);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return null;
            }

            user = new PanelUser(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), ReadPermissions(reader, 6), reader.IsDBNull(4) ? null : reader.GetInt64(4), ReadBool(reader, 5));
            expiresAt = ReadDate(reader, 3).ToUniversalTime();
        }

        if (expiresAt <= DateTime.UtcNow)
        {
            await RevokeUserSessionAsync(token);
            return null;
        }

        await ExecuteAsync("""
            UPDATE user_sessions
            SET last_seen_at = @last_seen_at
            WHERE token_hash = @token_hash
            """,
            ("@last_seen_at", DateTime.UtcNow),
            ("@token_hash", tokenHash));

        return user;
    }

    public async Task RevokeUserSessionAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        await ExecuteAsync("DELETE FROM user_sessions WHERE token_hash = @token_hash", ("@token_hash", HashSessionToken(token)));
    }

    public async Task RevokeUserSessionsAsync(long userId)
    {
        await ExecuteAsync("DELETE FROM user_sessions WHERE user_id = @user_id", ("@user_id", userId));
    }


    public async Task<IReadOnlyList<PanelUserSummary>> GetUsersAsync()
    {
        var users = new List<PanelUserSummary>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT u.id, u.username, u.role, COALESCE(u.email, ''), u.created_at, COUNT(a.id) AS assigned_vm_count,
                   u.parent_user_id, COALESCE(parent.username, ''), u.password_must_change,
                   u.can_view_console, u.can_view_resources, u.can_view_access, u.can_view_network,
                   u.can_view_backups, u.can_request_reinstall, u.can_view_activity, u.can_view_settings, u.can_use_power_actions
            FROM users u
            LEFT JOIN user_vm_assignments a ON a.user_id = u.id
            LEFT JOIN users parent ON parent.id = u.parent_user_id
            GROUP BY u.id, u.username, u.role, u.email, u.created_at,
                     u.parent_user_id, parent.username, u.password_must_change,
                     u.can_view_console, u.can_view_resources, u.can_view_access, u.can_view_network,
                     u.can_view_backups, u.can_request_reinstall, u.can_view_activity, u.can_view_settings, u.can_use_power_actions
            ORDER BY u.username 
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            users.Add(new PanelUserSummary(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                ReadDate(reader, 4),
                Convert.ToInt32(reader.GetInt64(5)),
                ReadPermissions(reader, 9),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                ReadBool(reader, 8)));
        }

        return users;
    }

    public async Task<IReadOnlyList<PanelUserSummary>> GetSubUsersAsync(long parentUserId)
    {
        var users = new List<PanelUserSummary>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT u.id, u.username, u.role, COALESCE(u.email, ''), u.created_at, COUNT(a.id) AS assigned_vm_count,
                   u.parent_user_id, COALESCE(parent.username, ''), u.password_must_change,
                   u.can_view_console, u.can_view_resources, u.can_view_access, u.can_view_network,
                   u.can_view_backups, u.can_request_reinstall, u.can_view_activity, u.can_view_settings, u.can_use_power_actions
            FROM users u
            LEFT JOIN user_vm_assignments a ON a.user_id = u.id
            LEFT JOIN users parent ON parent.id = u.parent_user_id
            WHERE u.parent_user_id = @parent_user_id
            GROUP BY u.id, u.username, u.role, u.email, u.created_at,
                     u.parent_user_id, parent.username, u.password_must_change,
                     u.can_view_console, u.can_view_resources, u.can_view_access, u.can_view_network,
                     u.can_view_backups, u.can_request_reinstall, u.can_view_activity, u.can_view_settings, u.can_use_power_actions
            ORDER BY u.username 
            """;
        cmd.Parameters.AddWithValue("@parent_user_id", parentUserId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            users.Add(new PanelUserSummary(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                ReadDate(reader, 4),
                Convert.ToInt32(reader.GetInt64(5)),
                ReadPermissions(reader, 9),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                ReadBool(reader, 8)));
        }

        return users;
    }

    public async Task<PanelUser?> GetUserByIdAsync(long userId)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, username, role, parent_user_id, password_must_change,
                   can_view_console, can_view_resources, can_view_access, can_view_network,
                   can_view_backups, can_request_reinstall, can_view_activity, can_view_settings, can_use_power_actions
            FROM users
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", userId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new PanelUser(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), ReadPermissions(reader, 5), reader.IsDBNull(3) ? null : reader.GetInt64(3), ReadBool(reader, 4));
    }

    public async Task<PanelUser?> GetUserByUsernameAsync(string username)
    {
        username = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, username, role, parent_user_id, password_must_change,
                   can_view_console, can_view_resources, can_view_access, can_view_network,
                   can_view_backups, can_request_reinstall, can_view_activity, can_view_settings, can_use_power_actions
            FROM users
            WHERE lower(username) = lower(@username)
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@username", username);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new PanelUser(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), ReadPermissions(reader, 5), reader.IsDBNull(3) ? null : reader.GetInt64(3), ReadBool(reader, 4));
    }

    public async Task<PanelUser?> GetUserByEmailAsync(string email)
    {
        email = (email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, username, role, parent_user_id, password_must_change,
                   can_view_console, can_view_resources, can_view_access, can_view_network,
                   can_view_backups, can_request_reinstall, can_view_activity, can_view_settings, can_use_power_actions
            FROM users
            WHERE lower(email) = lower(@email)
            ORDER BY id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@email", email);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new PanelUser(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), ReadPermissions(reader, 5), reader.IsDBNull(3) ? null : reader.GetInt64(3), ReadBool(reader, 4));
    }

    public async Task<PasswordRecoveryUser?> GetPasswordRecoveryUserAsync(string username)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, username, role, COALESCE(email, '')
            FROM users
            WHERE lower(username) = lower(@username)
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@username", username.Trim());
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new PasswordRecoveryUser(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
    }

    public async Task<long> CountAdminsAsync()
    {
        return await ScalarAsync("SELECT COUNT(*) FROM users WHERE lower(role) = 'admin'");
    }

    public async Task<long> CreateUserAsync(string username, string password, string role, string email = "", UserVmPermissions? permissions = null, long? parentUserId = null, bool mustChangePassword = true)
    {
        permissions ??= UserVmPermissions.ClientDefault;
        if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            permissions = UserVmPermissions.Admin;
        }

        await ExecuteAsync("""
            INSERT INTO users (username, password_hash, role, email, parent_user_id, password_must_change,
                               can_view_console, can_view_resources, can_view_access, can_view_network,
                               can_view_backups, can_request_reinstall, can_view_activity, can_view_settings, can_use_power_actions)
            VALUES (@username, @password_hash, @role, @email, @parent_user_id, @password_must_change,
                    @can_view_console, @can_view_resources, @can_view_access, @can_view_network,
                    @can_view_backups, @can_request_reinstall, @can_view_activity, @can_view_settings, @can_use_power_actions)
            """,
            ("@username", username),
            ("@password_hash", BCrypt.Net.BCrypt.HashPassword(password)),
            ("@role", role),
            ("@email", email ?? string.Empty),
            ("@parent_user_id", parentUserId is null ? DBNull.Value : parentUserId.Value),
            ("@password_must_change", ToInt(mustChangePassword)),
            ("@can_view_console", ToInt(permissions.CanViewConsole)),
            ("@can_view_resources", ToInt(permissions.CanViewResources)),
            ("@can_view_access", ToInt(permissions.CanViewAccess)),
            ("@can_view_network", ToInt(permissions.CanViewNetwork)),
            ("@can_view_backups", ToInt(permissions.CanViewBackups)),
            ("@can_request_reinstall", ToInt(permissions.CanRequestReinstall)),
            ("@can_view_activity", ToInt(permissions.CanViewActivity)),
            ("@can_view_settings", ToInt(permissions.CanViewSettings)),
            ("@can_use_power_actions", ToInt(permissions.CanUsePowerActions)));

        return await ScalarAsync("SELECT id FROM users WHERE username = @username", ("@username", username));
    }

    public async Task UpdateUserPasswordAsync(long userId, string password, bool mustChangePassword = false)
    {
        await ExecuteAsync("""
            UPDATE users
            SET password_hash = @password_hash,
                password_must_change = @password_must_change
            WHERE id = @id
            """,
            ("@id", userId),
            ("@password_hash", BCrypt.Net.BCrypt.HashPassword(password)),
            ("@password_must_change", ToInt(mustChangePassword)));
    }

    public async Task UpdateUserRoleAsync(long userId, string role)
    {
        await ExecuteAsync("""
            UPDATE users
            SET role = @role
            WHERE id = @id
            """,
            ("@id", userId),
            ("@role", role));
    }

    public async Task UpdateUserEmailAsync(long userId, string email)
    {
        await ExecuteAsync("UPDATE users SET email = @email WHERE id = @id", ("@id", userId), ("@email", email ?? string.Empty));
    }

    public async Task UpdateUserPermissionsAsync(long userId, UserVmPermissions permissions)
    {
        await ExecuteAsync("""
            UPDATE users
            SET can_view_console = @can_view_console,
                can_view_resources = @can_view_resources,
                can_view_access = @can_view_access,
                can_view_network = @can_view_network,
                can_view_backups = @can_view_backups,
                can_request_reinstall = @can_request_reinstall,
                can_view_activity = @can_view_activity,
                can_view_settings = @can_view_settings,
                can_use_power_actions = @can_use_power_actions
            WHERE id = @id
            """,
            ("@id", userId),
            ("@can_view_console", ToInt(permissions.CanViewConsole)),
            ("@can_view_resources", ToInt(permissions.CanViewResources)),
            ("@can_view_access", ToInt(permissions.CanViewAccess)),
            ("@can_view_network", ToInt(permissions.CanViewNetwork)),
            ("@can_view_backups", ToInt(permissions.CanViewBackups)),
            ("@can_request_reinstall", ToInt(permissions.CanRequestReinstall)),
            ("@can_view_activity", ToInt(permissions.CanViewActivity)),
            ("@can_view_settings", ToInt(permissions.CanViewSettings)),
            ("@can_use_power_actions", ToInt(permissions.CanUsePowerActions)));
    }

    public async Task<string> GetUserEmailAsync(long userId)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(email, '') FROM users WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", userId);
        return Convert.ToString(await cmd.ExecuteScalarAsync()) ?? string.Empty;
    }

    public async Task<IReadOnlyList<string>> GetAdminEmailsAsync()
    {
        var emails = new List<string>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT email FROM users WHERE lower(role) = 'admin' AND email IS NOT NULL AND trim(email) <> ''";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            emails.Add(reader.GetString(0));
        }

        return emails;
    }

    public async Task DeleteUserAsync(long userId)
    {
        await ExecuteAsync("DELETE FROM users WHERE parent_user_id = @id", ("@id", userId));
        await ExecuteAsync("DELETE FROM users WHERE id = @id", ("@id", userId));
    }

    public async Task<IReadOnlyList<UserVmAssignment>> GetUserVmAssignmentsAsync(long? userId = null)
    {
        var assignments = new List<UserVmAssignment>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = userId is null
            ? """
              SELECT a.id, a.user_id, u.username, a.vm_name, a.created_at
              FROM user_vm_assignments a
              INNER JOIN users u ON u.id = a.user_id
              ORDER BY u.username , a.vm_name 
              """
            : """
              SELECT a.id, a.user_id, u.username, a.vm_name, a.created_at
              FROM user_vm_assignments a
              INNER JOIN users u ON u.id = a.user_id
              WHERE a.user_id = @user_id
              ORDER BY a.vm_name 
              """;
        if (userId is not null)
        {
            cmd.Parameters.AddWithValue("@user_id", userId.Value);
        }

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            assignments.Add(new UserVmAssignment(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadDate(reader, 4)));
        }

        return assignments;
    }

    public async Task<HashSet<string>> GetAssignedVmNamesAsync(long userId)
    {
        var vmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT vm_name FROM user_vm_assignments WHERE user_id = @user_id";
        cmd.Parameters.AddWithValue("@user_id", userId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            vmNames.Add(reader.GetString(0));
        }

        return vmNames;
    }

    public async Task<bool> IsVmAssignedToUserAsync(long userId, string vmName)
    {
        return await ScalarAsync("""
            SELECT COUNT(*)
            FROM user_vm_assignments
            WHERE user_id = @user_id AND vm_name = @vm_name 
            """,
            ("@user_id", userId),
            ("@vm_name", vmName)) > 0;
    }

    public async Task AssignVmToUserAsync(long userId, string vmName)
    {
        await ExecuteAsync("""
            INSERT IGNORE INTO user_vm_assignments (user_id, vm_name)
            VALUES (@user_id, @vm_name)
            """,
            ("@user_id", userId),
            ("@vm_name", vmName));
    }

    public async Task SetUserVmAssignmentsAsync(long userId, IEnumerable<string> vmNames)
    {
        await using var db = Open();
        await using var transaction = await db.BeginTransactionAsync();

        await using (var delete = db.CreateCommand())
        {
            delete.Transaction = (MySqlTransaction)transaction;
            delete.CommandText = "DELETE FROM user_vm_assignments WHERE user_id = @user_id";
            delete.Parameters.AddWithValue("@user_id", userId);
            await delete.ExecuteNonQueryAsync();
        }

        foreach (var vmName in vmNames.Select(item => item.Trim()).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var insert = db.CreateCommand();
            insert.Transaction = (MySqlTransaction)transaction;
            insert.CommandText = """
                INSERT IGNORE INTO user_vm_assignments (user_id, vm_name)
                VALUES (@user_id, @vm_name)
                """;
            insert.Parameters.AddWithValue("@user_id", userId);
            insert.Parameters.AddWithValue("@vm_name", vmName);
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task UnassignVmFromUserAsync(long assignmentId)
    {
        await ExecuteAsync("DELETE FROM user_vm_assignments WHERE id = @id", ("@id", assignmentId));
    }


    public async Task<VmAccessInfo> GetVmAccessInfoAsync(string vmName)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, hostname, ip_address, rdp_port, ssh_port, username, password_secret, notes, web_console_url, updated_at
            FROM vm_access_info
            WHERE vm_name = @vm_name 
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new VmAccessInfo(
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                _secrets.Unprotect(reader.IsDBNull(6) ? string.Empty : reader.GetString(6)),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                ReadDate(reader, 9));
        }

        return new VmAccessInfo(vmName, string.Empty, string.Empty, 3389, null, "Administrator", string.Empty, string.Empty, string.Empty, DateTime.UtcNow);
    }

    public async Task SaveVmAccessInfoAsync(VmAccessInfo info)
    {
        await ExecuteAsync("""
            INSERT INTO vm_access_info (vm_name, hostname, ip_address, rdp_port, ssh_port, username, password_secret, notes, web_console_url, updated_at)
            VALUES (@vm_name, @hostname, @ip_address, @rdp_port, @ssh_port, @username, @password_secret, @notes, @web_console_url, @updated_at)
            ON DUPLICATE KEY UPDATE
                hostname = VALUES(hostname),
                ip_address = VALUES(ip_address),
                rdp_port = VALUES(rdp_port),
                ssh_port = VALUES(ssh_port),
                username = VALUES(username),
                password_secret = VALUES(password_secret),
                notes = VALUES(notes),
                web_console_url = VALUES(web_console_url),
                updated_at = VALUES(updated_at)
            """,
            ("@vm_name", info.VmName),
            ("@hostname", info.Hostname ?? string.Empty),
            ("@ip_address", info.IpAddress ?? string.Empty),
            ("@rdp_port", info.RdpPort),
            ("@ssh_port", info.SshPort),
            ("@username", info.Username ?? string.Empty),
            ("@password_secret", _secrets.Protect(info.Password)),
            ("@notes", info.Notes ?? string.Empty),
            ("@web_console_url", info.WebConsoleUrl ?? string.Empty),
            ("@updated_at", DateTime.UtcNow));
    }

    public async Task<VmRecoveryAccountSecret> GetVmRecoveryAccountAsync(string vmName)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, is_enabled, username, password_secret,
                   last_rotated_at, last_used_at, last_error, updated_at
            FROM vm_recovery_accounts
            WHERE vm_name = @vm_name
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new VmRecoveryAccountSecret(
                reader.GetString(0),
                !reader.IsDBNull(1) && reader.GetBoolean(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                _secrets.Unprotect(reader.IsDBNull(3) ? string.Empty : reader.GetString(3)),
                reader.IsDBNull(4) ? null : ReadDate(reader, 4),
                reader.IsDBNull(5) ? null : ReadDate(reader, 5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                ReadDate(reader, 7));
        }

        return new VmRecoveryAccountSecret(
            vmName,
            false,
            string.Empty,
            string.Empty,
            null,
            null,
            string.Empty,
            DateTime.UtcNow);
    }

    public async Task SaveVmRecoveryAccountAsync(VmRecoveryAccountSecret recovery)
    {
        await ExecuteAsync("""
            INSERT INTO vm_recovery_accounts (
                vm_name, is_enabled, username, password_secret,
                last_rotated_at, last_used_at, last_error, updated_at
            )
            VALUES (
                @vm_name, @is_enabled, @username, @password_secret,
                @last_rotated_at, @last_used_at, @last_error, @updated_at
            )
            ON DUPLICATE KEY UPDATE
                is_enabled = VALUES(is_enabled),
                username = VALUES(username),
                password_secret = VALUES(password_secret),
                last_rotated_at = VALUES(last_rotated_at),
                last_used_at = VALUES(last_used_at),
                last_error = VALUES(last_error),
                updated_at = VALUES(updated_at)
            """,
            ("@vm_name", recovery.VmName),
            ("@is_enabled", recovery.IsEnabled ? 1 : 0),
            ("@username", recovery.Username ?? string.Empty),
            ("@password_secret", _secrets.Protect(recovery.Password)),
            ("@last_rotated_at", recovery.LastRotatedAt),
            ("@last_used_at", recovery.LastUsedAt),
            ("@last_error", recovery.LastError ?? string.Empty),
            ("@updated_at", DateTime.UtcNow));
    }

    public async Task SetVmRecoveryAccountErrorAsync(string vmName, string username, string error)
    {
        await ExecuteAsync("""
            INSERT INTO vm_recovery_accounts (
                vm_name, is_enabled, username, password_secret,
                last_error, updated_at
            )
            VALUES (
                @vm_name, 0, @username, @password_secret,
                @last_error, @updated_at
            )
            ON DUPLICATE KEY UPDATE
                last_error = VALUES(last_error),
                updated_at = VALUES(updated_at)
            """,
            ("@vm_name", vmName),
            ("@username", username ?? string.Empty),
            ("@password_secret", _secrets.Protect(string.Empty)),
            ("@last_error", error ?? string.Empty),
            ("@updated_at", DateTime.UtcNow));
    }

    public async Task<VmNetworkPortProfile> GetVmNetworkPortProfileAsync(string vmName)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, nat_name, internal_ip_address, public_ip_address, public_hostname,
                   enable_rdp, rdp_external_port, rdp_internal_port,
                   enable_fivem, fivem_external_port, fivem_internal_port,
                   extra_tcp_ports, extra_udp_ports, dns_interface_alias,
                   preferred_dns_server, alternate_dns_server, updated_at
            FROM vm_network_profiles
            WHERE vm_name = @vm_name 
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new VmNetworkPortProfile(
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                !reader.IsDBNull(5) && reader.GetInt64(5) == 1,
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? 3389 : reader.GetInt32(7),
                !reader.IsDBNull(8) && reader.GetInt64(8) == 1,
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? 30120 : reader.GetInt32(10),
                reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
                reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
                ReadDate(reader, 16));
        }

        var access = await GetVmAccessInfoAsync(vmName);
        return new VmNetworkPortProfile(
            vmName,
            string.Empty,
            string.Empty,
            access.IpAddress ?? string.Empty,
            access.Hostname ?? string.Empty,
            true,
            access.RdpPort is > 0 ? access.RdpPort : null,
            3389,
            false,
            30120,
            30120,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            DateTime.UtcNow);
    }

    public async Task SaveVmNetworkPortProfileAsync(VmNetworkPortProfile profile)
    {
        await ExecuteAsync("""
            INSERT INTO vm_network_profiles (
                vm_name, nat_name, internal_ip_address, public_ip_address, public_hostname,
                enable_rdp, rdp_external_port, rdp_internal_port,
                enable_fivem, fivem_external_port, fivem_internal_port,
                extra_tcp_ports, extra_udp_ports, dns_interface_alias,
                preferred_dns_server, alternate_dns_server, updated_at)
            VALUES (
                @vm_name, @nat_name, @internal_ip_address, @public_ip_address, @public_hostname,
                @enable_rdp, @rdp_external_port, @rdp_internal_port,
                @enable_fivem, @fivem_external_port, @fivem_internal_port,
                @extra_tcp_ports, @extra_udp_ports, @dns_interface_alias,
                @preferred_dns_server, @alternate_dns_server, @updated_at)
            ON DUPLICATE KEY UPDATE
                nat_name = VALUES(nat_name),
                internal_ip_address = VALUES(internal_ip_address),
                public_ip_address = VALUES(public_ip_address),
                public_hostname = VALUES(public_hostname),
                enable_rdp = VALUES(enable_rdp),
                rdp_external_port = VALUES(rdp_external_port),
                rdp_internal_port = VALUES(rdp_internal_port),
                enable_fivem = VALUES(enable_fivem),
                fivem_external_port = VALUES(fivem_external_port),
                fivem_internal_port = VALUES(fivem_internal_port),
                extra_tcp_ports = VALUES(extra_tcp_ports),
                extra_udp_ports = VALUES(extra_udp_ports),
                dns_interface_alias = VALUES(dns_interface_alias),
                preferred_dns_server = VALUES(preferred_dns_server),
                alternate_dns_server = VALUES(alternate_dns_server),
                updated_at = VALUES(updated_at)
            """,
            ("@vm_name", profile.VmName),
            ("@nat_name", profile.NatName ?? string.Empty),
            ("@internal_ip_address", profile.InternalIpAddress ?? string.Empty),
            ("@public_ip_address", profile.PublicIpAddress ?? string.Empty),
            ("@public_hostname", profile.PublicHostname ?? string.Empty),
            ("@enable_rdp", profile.EnableRdp ? 1 : 0),
            ("@rdp_external_port", profile.RdpExternalPort),
            ("@rdp_internal_port", profile.RdpInternalPort <= 0 ? 3389 : profile.RdpInternalPort),
            ("@enable_fivem", profile.EnableFiveM ? 1 : 0),
            ("@fivem_external_port", profile.FiveMExternalPort),
            ("@fivem_internal_port", profile.FiveMInternalPort <= 0 ? 30120 : profile.FiveMInternalPort),
            ("@extra_tcp_ports", profile.ExtraTcpPorts ?? string.Empty),
            ("@extra_udp_ports", profile.ExtraUdpPorts ?? string.Empty),
            ("@dns_interface_alias", profile.DnsInterfaceAlias ?? string.Empty),
            ("@preferred_dns_server", profile.PreferredDnsServer ?? string.Empty),
            ("@alternate_dns_server", profile.AlternateDnsServer ?? string.Empty),
            ("@updated_at", DateTime.UtcNow));
    }

    public async Task DeleteStaleVmManagementStateAsync(string vmName)
    {
        vmName = (vmName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(vmName))
        {
            return;
        }

        await using var db = Open();
        await using var transaction = await db.BeginTransactionAsync();

        // Deliberately limited to active panel-management state. WHMCS rows,
        // audit history, abuse/bandwidth/power history, reinstall history and
        // all filesystem/VHDX data are preserved.
        var tables = new[]
        {
            "guest_agent_commands",
            "vm_guest_agents",
            "vm_recovery_accounts",
            "vm_public_status",
            "vm_network_profiles",
            "vm_access_info",
            "vm_availability_events",
            "vm_availability_state",
            "vm_availability_preferences",
            "vm_self_healing_events",
            "vm_self_healing_state",
            "vm_self_healing_preferences",
            "user_vm_assignments"
        };

        foreach (var table in tables)
        {
            await using var cmd = db.CreateCommand();
            cmd.Transaction = (MySqlTransaction)transaction;
            cmd.CommandText = $"DELETE FROM {table} WHERE vm_name = @vm_name";
            cmd.Parameters.AddWithValue("@vm_name", vmName);
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public Task DeleteVmRecoveryAccountAsync(string vmName) =>
        ExecuteAsync(
            "DELETE FROM vm_recovery_accounts WHERE vm_name = @vm_name",
            ("@vm_name", (vmName ?? string.Empty).Trim()));

    public async Task DeleteVmDataAsync(string vmName)
    {
        await using var db = Open();
        await using var transaction = await db.BeginTransactionAsync();

        var tables = new[]
        {
            "guest_agent_commands",
            "vm_guest_agents",
            "vm_recovery_accounts",
            "reinstall_requests",
            "vm_public_status",
            "vm_network_profiles",
            "vm_access_info",
            "abuse_events",
            "bandwidth_history",
            "vm_availability_events",
            "vm_availability_state",
            "vm_availability_preferences",
            "vm_self_healing_events",
            "vm_self_healing_state",
            "vm_self_healing_preferences",
            "vm_performance_history",
            "user_power_actions",
            "user_vm_assignments"
        };

        foreach (var table in tables)
        {
            await using var cmd = db.CreateCommand();
            cmd.Transaction = (MySqlTransaction)transaction;
            cmd.CommandText = $"DELETE FROM {table} WHERE vm_name = @vm_name";
            cmd.Parameters.AddWithValue("@vm_name", vmName);
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }


    public async Task<IReadOnlyList<VmQuickSnapshotMetadata>> GetQuickSnapshotMetadataAsync(string vmName)
    {
        var result = new List<VmQuickSnapshotMetadata>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, snapshot_id, snapshot_name, COALESCE(description, ''),
                   is_pinned, is_temporary_safety, expires_at, created_by_user_id,
                   created_by_username, created_at, updated_at
            FROM vm_quick_snapshot_metadata
            WHERE vm_name = @vm_name
            ORDER BY created_at DESC
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new VmQuickSnapshotMetadata(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                !reader.IsDBNull(4) && Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture) != 0,
                !reader.IsDBNull(5) && Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture) != 0,
                reader.IsDBNull(6) ? null : ReadDate(reader, 6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                ReadDate(reader, 9),
                ReadDate(reader, 10)));
        }

        return result;
    }

    public Task UpsertQuickSnapshotMetadataAsync(VmQuickSnapshotMetadata item) =>
        ExecuteAsync("""
            INSERT INTO vm_quick_snapshot_metadata (
                vm_name, snapshot_id, snapshot_name, description, is_pinned,
                is_temporary_safety, expires_at, created_by_user_id,
                created_by_username, created_at, updated_at)
            VALUES (
                @vm_name, @snapshot_id, @snapshot_name, @description, @is_pinned,
                @is_temporary_safety, @expires_at, @created_by_user_id,
                @created_by_username, @created_at, @updated_at)
            ON DUPLICATE KEY UPDATE
                snapshot_name = VALUES(snapshot_name),
                description = VALUES(description),
                is_pinned = VALUES(is_pinned),
                is_temporary_safety = VALUES(is_temporary_safety),
                expires_at = VALUES(expires_at),
                created_by_user_id = VALUES(created_by_user_id),
                created_by_username = VALUES(created_by_username),
                updated_at = VALUES(updated_at)
            """,
            ("@vm_name", item.VmName),
            ("@snapshot_id", item.SnapshotId),
            ("@snapshot_name", item.SnapshotName),
            ("@description", item.Description),
            ("@is_pinned", item.IsPinned ? 1 : 0),
            ("@is_temporary_safety", item.IsTemporarySafety ? 1 : 0),
            ("@expires_at", item.ExpiresAt),
            ("@created_by_user_id", item.CreatedByUserId),
            ("@created_by_username", item.CreatedByUsername),
            ("@created_at", item.CreatedAt),
            ("@updated_at", item.UpdatedAt));

    public Task SetQuickSnapshotPinnedAsync(string vmName, string snapshotId, bool isPinned) =>
        ExecuteAsync("""
            UPDATE vm_quick_snapshot_metadata
            SET is_pinned = @is_pinned,
                updated_at = CURRENT_TIMESTAMP(6)
            WHERE vm_name = @vm_name AND snapshot_id = @snapshot_id
            """,
            ("@vm_name", vmName),
            ("@snapshot_id", snapshotId),
            ("@is_pinned", isPinned ? 1 : 0));

    public async Task SetPermanentQuickSnapshotAsync(string vmName, string snapshotId)
    {
        vmName = (vmName ?? string.Empty).Trim();
        snapshotId = (snapshotId ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(vmName) || string.IsNullOrWhiteSpace(snapshotId))
        {
            throw new InvalidOperationException("Permanent restore point requires a VM and snapshot ID.");
        }

        await using var db = Open();
        await using var transaction = await db.BeginTransactionAsync();

        await using (var clear = db.CreateCommand())
        {
            clear.Transaction = (MySqlTransaction)transaction;
            clear.CommandText = """
                UPDATE vm_quick_snapshot_metadata
                SET is_pinned = 0,
                    updated_at = CURRENT_TIMESTAMP(6)
                WHERE vm_name = @vm_name
                """;
            clear.Parameters.AddWithValue("@vm_name", vmName);
            await clear.ExecuteNonQueryAsync();
        }

        await using (var set = db.CreateCommand())
        {
            set.Transaction = (MySqlTransaction)transaction;
            set.CommandText = """
                UPDATE vm_quick_snapshot_metadata
                SET is_pinned = 1,
                    updated_at = CURRENT_TIMESTAMP(6)
                WHERE vm_name = @vm_name
                  AND snapshot_id = @snapshot_id
                """;
            set.Parameters.AddWithValue("@vm_name", vmName);
            set.Parameters.AddWithValue("@snapshot_id", snapshotId);

            var affected = await set.ExecuteNonQueryAsync();
            if (affected != 1)
            {
                throw new InvalidOperationException(
                    "The selected restore point metadata disappeared before it could be made permanent.");
            }
        }

        await transaction.CommitAsync();
    }

    public Task DeleteQuickSnapshotMetadataAsync(string vmName, string snapshotId) =>
        ExecuteAsync("""
            DELETE FROM vm_quick_snapshot_metadata
            WHERE vm_name = @vm_name AND snapshot_id = @snapshot_id
            """,
            ("@vm_name", vmName),
            ("@snapshot_id", snapshotId));

    public async Task<long> CreateVmReinstallRollbackAsync(
        VmReinstallRollbackInfo rollback,
        VmAccessInfo previousAccess,
        long? createdByUserId,
        string createdByUsername)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vm_reinstall_rollbacks (
                vm_name, template_name, rollback_disk_path, reinstalled_disk_path,
                virtual_disk_bytes, physical_disk_bytes, status, was_running,
                previous_state, old_hostname, old_ip_address, old_rdp_port,
                old_ssh_port, old_username, old_password_secret, old_notes,
                old_web_console_url, created_by_user_id, created_by_username,
                created_at, expires_at, restored_at, deleted_at, last_error)
            VALUES (
                @vm_name, @template_name, @rollback_disk_path, @reinstalled_disk_path,
                @virtual_disk_bytes, @physical_disk_bytes, @status, @was_running,
                @previous_state, @old_hostname, @old_ip_address, @old_rdp_port,
                @old_ssh_port, @old_username, @old_password_secret, @old_notes,
                @old_web_console_url, @created_by_user_id, @created_by_username,
                @created_at, @expires_at, @restored_at, @deleted_at, @last_error)
            """;

        cmd.Parameters.AddWithValue("@vm_name", rollback.VmName);
        cmd.Parameters.AddWithValue("@template_name", rollback.TemplateName ?? string.Empty);
        cmd.Parameters.AddWithValue("@rollback_disk_path", rollback.RollbackDiskPath ?? string.Empty);
        cmd.Parameters.AddWithValue("@reinstalled_disk_path", rollback.ReinstalledDiskPath ?? string.Empty);
        cmd.Parameters.AddWithValue("@virtual_disk_bytes", rollback.VirtualDiskBytes);
        cmd.Parameters.AddWithValue("@physical_disk_bytes", rollback.PhysicalDiskBytes);
        cmd.Parameters.AddWithValue("@status", rollback.Status ?? "available");
        cmd.Parameters.AddWithValue("@was_running", rollback.WasRunning ? 1 : 0);
        cmd.Parameters.AddWithValue("@previous_state", rollback.PreviousState ?? string.Empty);
        cmd.Parameters.AddWithValue("@old_hostname", previousAccess.Hostname ?? string.Empty);
        cmd.Parameters.AddWithValue("@old_ip_address", previousAccess.IpAddress ?? string.Empty);
        cmd.Parameters.AddWithValue("@old_rdp_port", previousAccess.RdpPort);
        cmd.Parameters.AddWithValue("@old_ssh_port", previousAccess.SshPort);
        cmd.Parameters.AddWithValue("@old_username", previousAccess.Username ?? string.Empty);
        cmd.Parameters.AddWithValue(
            "@old_password_secret",
            _secrets.Protect(previousAccess.Password ?? string.Empty));
        cmd.Parameters.AddWithValue("@old_notes", previousAccess.Notes ?? string.Empty);
        cmd.Parameters.AddWithValue("@old_web_console_url", previousAccess.WebConsoleUrl ?? string.Empty);
        cmd.Parameters.AddWithValue("@created_by_user_id", createdByUserId);
        cmd.Parameters.AddWithValue("@created_by_username", createdByUsername ?? string.Empty);
        cmd.Parameters.AddWithValue("@created_at", rollback.CreatedAt);
        cmd.Parameters.AddWithValue("@expires_at", rollback.ExpiresAt);
        cmd.Parameters.AddWithValue("@restored_at", rollback.RestoredAt);
        cmd.Parameters.AddWithValue("@deleted_at", rollback.DeletedAt);
        cmd.Parameters.AddWithValue("@last_error", rollback.LastError ?? string.Empty);

        await cmd.ExecuteNonQueryAsync();
        return cmd.LastInsertedId;
    }

    public async Task<VmReinstallRollbackInfo?> GetAvailableVmReinstallRollbackAsync(
        string vmName)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, vm_name, template_name, rollback_disk_path,
                   reinstalled_disk_path, virtual_disk_bytes, physical_disk_bytes,
                   status, was_running, previous_state, created_at, expires_at,
                   restored_at, deleted_at, COALESCE(last_error, '')
            FROM vm_reinstall_rollbacks
            WHERE vm_name = @vm_name
              AND status = 'available'
            ORDER BY created_at DESC, id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? ReadVmReinstallRollback(reader)
            : null;
    }

    public async Task<IReadOnlyList<VmReinstallRollbackInfo>> GetExpiredVmReinstallRollbacksAsync(
        DateTime expiresBeforeUtc,
        int limit)
    {
        var result = new List<VmReinstallRollbackInfo>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, vm_name, template_name, rollback_disk_path,
                   reinstalled_disk_path, virtual_disk_bytes, physical_disk_bytes,
                   status, was_running, previous_state, created_at, expires_at,
                   restored_at, deleted_at, COALESCE(last_error, '')
            FROM vm_reinstall_rollbacks
            WHERE status = 'available'
              AND expires_at <= @expires_before
            ORDER BY expires_at ASC, id ASC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@expires_before", expiresBeforeUtc);
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 2000));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(ReadVmReinstallRollback(reader));
        }

        return result;
    }

    public async Task<VmReinstallRollbackSecret?> GetVmReinstallRollbackSecretAsync(
        long rollbackId)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, vm_name, template_name, rollback_disk_path,
                   reinstalled_disk_path, virtual_disk_bytes, physical_disk_bytes,
                   status, was_running, previous_state, created_at, expires_at,
                   restored_at, deleted_at, COALESCE(last_error, ''),
                   old_hostname, old_ip_address, old_rdp_port, old_ssh_port,
                   old_username, old_password_secret, old_notes, old_web_console_url
            FROM vm_reinstall_rollbacks
            WHERE id = @id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@id", rollbackId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var info = ReadVmReinstallRollback(reader);
        var access = new VmAccessInfo(
            info.VmName,
            reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
            reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetInt32(17),
            reader.IsDBNull(18) ? null : reader.GetInt32(18),
            reader.IsDBNull(19) ? "Administrator" : reader.GetString(19),
            _secrets.Unprotect(reader.IsDBNull(20) ? string.Empty : reader.GetString(20)),
            reader.IsDBNull(21) ? string.Empty : reader.GetString(21),
            reader.IsDBNull(22) ? string.Empty : reader.GetString(22),
            DateTime.UtcNow);

        return new VmReinstallRollbackSecret(info, access);
    }

    public Task SetVmReinstallRollbackExpiryAsync(
        long rollbackId,
        DateTime expiresAt) =>
        ExecuteAsync("""
            UPDATE vm_reinstall_rollbacks
            SET expires_at = @expires_at
            WHERE id = @id AND status = 'available'
            """,
            ("@id", rollbackId),
            ("@expires_at", expiresAt));

    public Task MarkVmReinstallRollbackRestoredAsync(
        long rollbackId,
        string lastError = "") =>
        ExecuteAsync("""
            UPDATE vm_reinstall_rollbacks
            SET status = 'restored',
                restored_at = @restored_at,
                old_password_secret = '',
                last_error = @last_error
            WHERE id = @id
            """,
            ("@id", rollbackId),
            ("@restored_at", DateTime.UtcNow),
            ("@last_error", lastError ?? string.Empty));

    public Task MarkVmReinstallRollbackDeletedAsync(
        long rollbackId,
        string status,
        string lastError = "") =>
        ExecuteAsync("""
            UPDATE vm_reinstall_rollbacks
            SET status = @status,
                deleted_at = @deleted_at,
                old_password_secret = '',
                last_error = @last_error
            WHERE id = @id
            """,
            ("@id", rollbackId),
            ("@status", string.IsNullOrWhiteSpace(status) ? "deleted" : status.Trim().ToLowerInvariant()),
            ("@deleted_at", DateTime.UtcNow),
            ("@last_error", lastError ?? string.Empty));

    public Task SetVmReinstallRollbackErrorAsync(
        long rollbackId,
        string lastError) =>
        ExecuteAsync("""
            UPDATE vm_reinstall_rollbacks
            SET last_error = @last_error
            WHERE id = @id
            """,
            ("@id", rollbackId),
            ("@last_error", lastError ?? string.Empty));

    private static VmReinstallRollbackInfo ReadVmReinstallRollback(
        MySqlDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
            reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
            reader.IsDBNull(7) ? "available" : reader.GetString(7),
            !reader.IsDBNull(8)
                && Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture) != 0,
            reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            ReadDate(reader, 10),
            ReadDate(reader, 11),
            reader.IsDBNull(12) ? null : ReadDate(reader, 12),
            reader.IsDBNull(13) ? null : ReadDate(reader, 13),
            reader.IsDBNull(14) ? string.Empty : reader.GetString(14));

    public async Task<long> CreateVmBackupRecordAsync(VmFullBackupInfo backup)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vm_backups (
                vm_name, name, backup_type, status, backup_path, manifest_path,
                total_bytes, disk_count, was_running, created_by_user_id,
                created_by_username, last_error, created_at, completed_at)
            VALUES (
                @vm_name, @name, @backup_type, @status, @backup_path, @manifest_path,
                @total_bytes, @disk_count, @was_running, @created_by_user_id,
                @created_by_username, @last_error, @created_at, @completed_at)
            """;
        cmd.Parameters.AddWithValue("@vm_name", backup.VmName);
        cmd.Parameters.AddWithValue("@name", backup.Name);
        cmd.Parameters.AddWithValue("@backup_type", backup.BackupType);
        cmd.Parameters.AddWithValue("@status", backup.Status);
        cmd.Parameters.AddWithValue("@backup_path", backup.BackupPath);
        cmd.Parameters.AddWithValue("@manifest_path", backup.ManifestPath);
        cmd.Parameters.AddWithValue("@total_bytes", backup.TotalBytes);
        cmd.Parameters.AddWithValue("@disk_count", backup.DiskCount);
        cmd.Parameters.AddWithValue("@was_running", backup.WasRunning ? 1 : 0);
        cmd.Parameters.AddWithValue("@created_by_user_id", backup.CreatedByUserId);
        cmd.Parameters.AddWithValue("@created_by_username", backup.CreatedByUsername ?? string.Empty);
        cmd.Parameters.AddWithValue("@last_error", backup.LastError ?? string.Empty);
        cmd.Parameters.AddWithValue("@created_at", backup.CreatedAt);
        cmd.Parameters.AddWithValue("@completed_at", backup.CompletedAt);
        await cmd.ExecuteNonQueryAsync();
        return cmd.LastInsertedId;
    }

    public async Task UpdateVmBackupRecordAsync(
        long backupId,
        string status,
        string backupPath,
        string manifestPath,
        long totalBytes,
        int diskCount,
        string lastError,
        DateTime? completedAt)
    {
        await ExecuteAsync("""
            UPDATE vm_backups
            SET status = @status,
                backup_path = @backup_path,
                manifest_path = @manifest_path,
                total_bytes = @total_bytes,
                disk_count = @disk_count,
                last_error = @last_error,
                completed_at = @completed_at
            WHERE id = @id
            """,
            ("@id", backupId),
            ("@status", status ?? string.Empty),
            ("@backup_path", backupPath ?? string.Empty),
            ("@manifest_path", manifestPath ?? string.Empty),
            ("@total_bytes", totalBytes),
            ("@disk_count", diskCount),
            ("@last_error", lastError ?? string.Empty),
            ("@completed_at", completedAt));
    }

    public async Task<IReadOnlyList<VmFullBackupInfo>> GetVmBackupsAsync(string vmName)
    {
        var result = new List<VmFullBackupInfo>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, vm_name, name, backup_type, status, backup_path, manifest_path,
                   total_bytes, disk_count, was_running, created_by_user_id,
                   created_by_username, COALESCE(last_error, ''), created_at, completed_at
            FROM vm_backups
            WHERE vm_name = @vm_name
            ORDER BY created_at DESC, id DESC
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(ReadVmBackup(reader));
        }

        return result;
    }

    public async Task<VmFullBackupInfo?> GetVmBackupAsync(long backupId)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, vm_name, name, backup_type, status, backup_path, manifest_path,
                   total_bytes, disk_count, was_running, created_by_user_id,
                   created_by_username, COALESCE(last_error, ''), created_at, completed_at
            FROM vm_backups
            WHERE id = @id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@id", backupId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadVmBackup(reader) : null;
    }

    public async Task<int> CountCompletedVmBackupsAsync(string vmName, string backupType)
    {
        return (int)await ScalarAsync("""
            SELECT COUNT(*)
            FROM vm_backups
            WHERE vm_name = @vm_name
              AND status = 'complete'
              AND backup_type = @backup_type
            """,
            ("@vm_name", vmName),
            ("@backup_type", backupType));
    }

    public Task DeleteVmBackupRecordAsync(long backupId) =>
        ExecuteAsync("DELETE FROM vm_backups WHERE id = @id", ("@id", backupId));

    private static VmFullBackupInfo ReadVmBackup(MySqlDataReader reader)
    {
        return new VmFullBackupInfo(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
            reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
            !reader.IsDBNull(9) && Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture) != 0,
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            ReadDate(reader, 13),
            reader.IsDBNull(14) ? null : ReadDate(reader, 14));
    }

    public async Task<VmTemplateBootstrapCredential> GetVmTemplateBootstrapCredentialAsync(
        long templateId)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT bootstrap_username, bootstrap_password_secret
            FROM vm_templates
            WHERE id = @id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@id", templateId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return new VmTemplateBootstrapCredential(
                templateId,
                "Administrator",
                string.Empty);
        }

        var username = reader.IsDBNull(0)
            ? "Administrator"
            : reader.GetString(0).Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            username = "Administrator";
        }

        var password = _secrets.Unprotect(
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1));

        return new VmTemplateBootstrapCredential(
            templateId,
            username,
            password);
    }

    public async Task<IReadOnlyList<VmTemplateBootstrapStatus>> GetVmTemplateBootstrapStatusesAsync()
    {
        var result = new List<VmTemplateBootstrapStatus>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, bootstrap_username, bootstrap_password_secret
            FROM vm_templates
            ORDER BY name
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var username = reader.IsDBNull(1)
                ? "Administrator"
                : reader.GetString(1).Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                username = "Administrator";
            }

            var protectedPassword = reader.IsDBNull(2)
                ? string.Empty
                : reader.GetString(2);
            var hasPassword = !string.IsNullOrWhiteSpace(
                _secrets.Unprotect(protectedPassword));

            result.Add(new VmTemplateBootstrapStatus(
                reader.GetInt64(0),
                username,
                hasPassword));
        }

        return result;
    }

    public async Task SaveVmTemplateBootstrapCredentialAsync(
        long templateId,
        string username,
        string password)
    {
        username = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            username = "Administrator";
        }

        password ??= string.Empty;

        if (string.IsNullOrWhiteSpace(password))
        {
            await ExecuteAsync("""
                UPDATE vm_templates
                SET bootstrap_username = @username
                WHERE id = @id
                """,
                ("@id", templateId),
                ("@username", username));
            return;
        }

        await ExecuteAsync("""
            UPDATE vm_templates
            SET bootstrap_username = @username,
                bootstrap_password_secret = @password_secret
            WHERE id = @id
            """,
            ("@id", templateId),
            ("@username", username),
            ("@password_secret", _secrets.Protect(password)));
    }

    public async Task<IReadOnlyList<VmTemplateValidationStatus>> GetVmTemplateValidationStatusesAsync()
    {
        var result = new List<VmTemplateValidationStatus>();

        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id,
                   vm_generation,
                   validation_status,
                   validation_message,
                   validation_windows_product,
                   validation_windows_version,
                   validation_build,
                   validation_architecture,
                   validation_powershell_direct,
                   validation_rdp_service,
                   validation_rdp_enabled,
                   validation_rdp_status,
                   validation_virtual_disk_bytes,
                   validation_physical_disk_bytes,
                   validation_file_last_write_ticks,
                   validation_checked_at
            FROM vm_templates
            ORDER BY name
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new VmTemplateValidationStatus(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? 2 : Math.Clamp(reader.GetInt32(1), 1, 2),
                reader.IsDBNull(2) ? "unverified" : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                ReadBool(reader, 8),
                ReadBool(reader, 9),
                ReadBool(reader, 10),
                reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                reader.IsDBNull(12) ? 0 : reader.GetInt64(12),
                reader.IsDBNull(13) ? 0 : reader.GetInt64(13),
                reader.IsDBNull(14) ? 0 : reader.GetInt64(14),
                reader.IsDBNull(15) ? null : ReadDate(reader, 15),
                false));
        }

        return result;
    }

    public async Task SaveVmTemplateGenerationAsync(long templateId, int generation)
    {
        generation = Math.Clamp(generation, 1, 2);

        await ExecuteAsync("""
            UPDATE vm_templates
            SET vm_generation = @generation
            WHERE id = @id
            """,
            ("@id", templateId),
            ("@generation", generation));
    }

    public async Task SaveVmTemplateValidationStatusAsync(
        VmTemplateValidationStatus status)
    {
        await ExecuteAsync("""
            UPDATE vm_templates
            SET validation_status = @status,
                validation_message = @message,
                validation_windows_product = @windows_product,
                validation_windows_version = @windows_version,
                validation_build = @build,
                validation_architecture = @architecture,
                validation_powershell_direct = @powershell_direct,
                validation_rdp_service = @rdp_service,
                validation_rdp_enabled = @rdp_enabled,
                validation_rdp_status = @rdp_status,
                validation_virtual_disk_bytes = @virtual_disk_bytes,
                validation_physical_disk_bytes = @physical_disk_bytes,
                validation_file_last_write_ticks = @file_last_write_ticks,
                validation_checked_at = @checked_at
            WHERE id = @id
            """,
            ("@id", status.TemplateId),
            ("@status", status.Status ?? "unverified"),
            ("@message", status.Message ?? string.Empty),
            ("@windows_product", status.WindowsProductName ?? string.Empty),
            ("@windows_version", status.WindowsVersion ?? string.Empty),
            ("@build", status.BuildNumber ?? string.Empty),
            ("@architecture", status.Architecture ?? string.Empty),
            ("@powershell_direct", status.PowerShellDirectOk ? 1 : 0),
            ("@rdp_service", status.RdpServiceFound ? 1 : 0),
            ("@rdp_enabled", status.RdpEnabled ? 1 : 0),
            ("@rdp_status", status.RdpServiceStatus ?? string.Empty),
            ("@virtual_disk_bytes", status.VirtualDiskBytes),
            ("@physical_disk_bytes", status.PhysicalDiskBytes),
            ("@file_last_write_ticks", status.TemplateLastWriteUtcTicks),
            ("@checked_at", status.CheckedAt));
    }

    public async Task InvalidateVmTemplateValidationAsync(
        long templateId,
        string reason)
    {
        await ExecuteAsync("""
            UPDATE vm_templates
            SET validation_status = 'unverified',
                validation_message = @message,
                validation_windows_product = '',
                validation_windows_version = '',
                validation_build = '',
                validation_architecture = '',
                validation_powershell_direct = 0,
                validation_rdp_service = 0,
                validation_rdp_enabled = 0,
                validation_rdp_status = '',
                validation_virtual_disk_bytes = 0,
                validation_physical_disk_bytes = 0,
                validation_file_last_write_ticks = 0,
                validation_checked_at = NULL
            WHERE id = @id
            """,
            ("@id", templateId),
            ("@message", reason ?? "Template changed; validate again."));
    }

    public async Task<IReadOnlyList<VmTemplate>> GetVmTemplatesAsync(bool enabledOnly = false)
    {
        var templates = new List<VmTemplate>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = enabledOnly
            ? "SELECT id, name, operating_system, iso_path, notes, is_enabled, created_at FROM vm_templates WHERE is_enabled = 1 ORDER BY name "
            : "SELECT id, name, operating_system, iso_path, notes, is_enabled, created_at FROM vm_templates ORDER BY name ";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            templates.Add(new VmTemplate(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.GetInt64(5) == 1,
                ReadDate(reader, 6)));
        }

        return templates;
    }

    public async Task<long> SaveVmTemplateAsync(VmTemplate template)
    {
        var name = (template.Name ?? string.Empty).Trim();

        if (template.Id <= 0)
        {
            var existingId = await ScalarAsync("SELECT id FROM vm_templates WHERE lower(name) = lower(@name) LIMIT 1", ("@name", name));
            if (existingId > 0)
            {
                await ExecuteAsync("""
                    UPDATE vm_templates
                    SET operating_system = @operating_system, iso_path = @iso_path, notes = @notes, is_enabled = @is_enabled
                    WHERE id = @id
                    """,
                    ("@id", existingId),
                    ("@operating_system", template.OperatingSystem ?? string.Empty),
                    ("@iso_path", template.IsoPath ?? string.Empty),
                    ("@notes", template.Notes ?? string.Empty),
                    ("@is_enabled", template.IsEnabled ? 1 : 0));
                return existingId;
            }

            await ExecuteAsync("""
                INSERT INTO vm_templates (name, operating_system, iso_path, notes, is_enabled)
                VALUES (@name, @operating_system, @iso_path, @notes, @is_enabled)
                """,
                ("@name", name),
                ("@operating_system", template.OperatingSystem ?? string.Empty),
                ("@iso_path", template.IsoPath ?? string.Empty),
                ("@notes", template.Notes ?? string.Empty),
                ("@is_enabled", template.IsEnabled ? 1 : 0));
            return await ScalarAsync("SELECT id FROM vm_templates WHERE name = @name", ("@name", name));
        }

        await ExecuteAsync("""
            UPDATE vm_templates
            SET name = @name, operating_system = @operating_system, iso_path = @iso_path, notes = @notes, is_enabled = @is_enabled
            WHERE id = @id
            """,
            ("@id", template.Id),
            ("@name", name),
            ("@operating_system", template.OperatingSystem ?? string.Empty),
            ("@iso_path", template.IsoPath ?? string.Empty),
            ("@notes", template.Notes ?? string.Empty),
            ("@is_enabled", template.IsEnabled ? 1 : 0));
        return template.Id;
    }

    public async Task DeleteVmTemplateAsync(long templateId)
    {
        await ExecuteAsync("DELETE FROM vm_templates WHERE id = @id", ("@id", templateId));
    }

    public async Task<long> CreateReinstallRequestAsync(long userId, string vmName, long templateId, string notes)
    {
        await ExecuteAsync("""
            INSERT INTO reinstall_requests (user_id, vm_name, template_id, status, notes)
            VALUES (@user_id, @vm_name, @template_id, 'pending', @notes)
            """,
            ("@user_id", userId),
            ("@vm_name", vmName),
            ("@template_id", templateId),
            ("@notes", notes ?? string.Empty));
        return await ScalarAsync("""
            SELECT id
            FROM reinstall_requests
            WHERE user_id = @user_id AND vm_name = @vm_name  AND template_id = @template_id
            ORDER BY id DESC
            LIMIT 1
            """,
            ("@user_id", userId),
            ("@vm_name", vmName),
            ("@template_id", templateId));
    }

    public async Task<IReadOnlyList<ReinstallRequest>> GetReinstallRequestsAsync(long? userId = null, string? vmName = null)
    {
        var requests = new List<ReinstallRequest>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.user_id, u.username, r.vm_name, r.template_id, t.name, r.status, r.notes, r.created_at
            FROM reinstall_requests r
            INNER JOIN users u ON u.id = r.user_id
            INNER JOIN vm_templates t ON t.id = r.template_id
            WHERE (@user_id IS NULL OR r.user_id = @user_id)
              AND (@vm_name IS NULL OR r.vm_name = @vm_name )
            ORDER BY r.id DESC
            LIMIT 100
            """;
        cmd.Parameters.AddWithValue("@user_id", userId is null ? DBNull.Value : (object)userId.Value);
        cmd.Parameters.AddWithValue("@vm_name", string.IsNullOrWhiteSpace(vmName) ? DBNull.Value : (object)vmName);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            requests.Add(new ReinstallRequest(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                ReadDate(reader, 8)));
        }

        return requests;
    }

    public async Task UpdateReinstallRequestStatusAsync(long requestId, string status)
    {
        await ExecuteAsync("UPDATE reinstall_requests SET status = @status WHERE id = @id", ("@id", requestId), ("@status", status));
    }

    public async Task<ReinstallJob?> GetReinstallJobAsync(long requestId)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.vm_name, r.template_id, t.name, t.iso_path, u.username, r.status
            FROM reinstall_requests r
            INNER JOIN users u ON u.id = r.user_id
            INNER JOIN vm_templates t ON t.id = r.template_id
            WHERE r.id = @id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@id", requestId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ReinstallJob(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6));
    }

    public async Task AddAuditAsync(string action, string resourceType, string? resourceId = null, string? details = null)
    {
        await ExecuteAsync("""
            INSERT INTO audit_logs (action, resource_type, resource_id, details)
            VALUES (@action, @resource_type, @resource_id, @details)
            """,
            ("@action", action),
            ("@resource_type", resourceType),
            ("@resource_id", resourceId ?? string.Empty),
            ("@details", details ?? string.Empty));
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogsAsync(int limit = 100)
    {
        var logs = new List<AuditLogEntry>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, action, resource_type, resource_id, details, created_at
            FROM audit_logs
            ORDER BY id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            logs.Add(new AuditLogEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ReadDate(reader, 5)));
        }

        return logs;
    }

    public async Task DeleteAuditLogAsync(long logId)
    {
        await ExecuteAsync("DELETE FROM audit_logs WHERE id = @id", ("@id", logId));
        await AddAuditAsync("AUDIT_LOG_DELETED", "audit_log", logId.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<int> ClearAuditLogsAsync()
    {
        var count = await ScalarAsync("SELECT COUNT(*) FROM audit_logs");
        await ExecuteAsync("DELETE FROM audit_logs");
        await AddAuditAsync("AUDIT_LOGS_CLEARED", "audit_log", null, $"Deleted {count} audit log(s)");
        return (int)Math.Min(count, int.MaxValue);
    }

    public async Task<int> DeleteOldAuditLogsAsync(int olderThanDays)
    {
        olderThanDays = Math.Clamp(olderThanDays, 1, 3650);
        var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
        var count = await ScalarAsync("SELECT COUNT(*) FROM audit_logs WHERE created_at < @cutoff", ("@cutoff", cutoff));
        await ExecuteAsync("DELETE FROM audit_logs WHERE created_at < @cutoff", ("@cutoff", cutoff));
        await AddAuditAsync("AUDIT_LOGS_PRUNED", "audit_log", null, $"Deleted {count} audit log(s) older than {olderThanDays} day(s)");
        return (int)Math.Min(count, int.MaxValue);
    }

    public async Task<AbuseConfig> GetAbuseConfigAsync()
    {
        var settings = await GetSettingsAsync();
        var outboundThreshold = NormalizeAbuseThreshold(ParseDoubleSetting(settings.GetValueOrDefault("abuse_outbound_threshold_mbps", "50"), 50), 50);
        var sustainedThreshold = NormalizeAbuseThreshold(ParseDoubleSetting(settings.GetValueOrDefault("abuse_sustained_threshold_mbps", "100"), 100), 100);
        sustainedThreshold = Math.Max(outboundThreshold, sustainedThreshold);

        return new AbuseConfig(
            settings.GetValueOrDefault("abuse_enabled", "1") == "1",
            settings.GetValueOrDefault("abuse_auto_disconnect", "0") == "1",
            settings.GetValueOrDefault("abuse_auto_suspend", "0") == "1",
            outboundThreshold,
            sustainedThreshold,
            settings.GetValueOrDefault("abuse_auto_scan_enabled", "0") == "1",
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("abuse_scan_interval_seconds", "30"), 30), 10, 3600),
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("abuse_cooldown_minutes", "10"), 10), 1, 1440),
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("abuse_required_consecutive_detections", "3"), 3), 1, 20));
    }

    public async Task SaveAbuseConfigAsync(AbuseConfig config)
    {
        var outboundThreshold = Math.Clamp(config.OutboundThresholdMbps, 1, 1_000_000);
        var sustainedThreshold = Math.Clamp(config.SustainedThresholdMbps, outboundThreshold, 1_000_000);

        await SetSettingAsync("abuse_enabled", config.Enabled ? "1" : "0");
        await SetSettingAsync("abuse_auto_disconnect", config.AutoDisconnect ? "1" : "0");
        await SetSettingAsync("abuse_auto_suspend", config.AutoSuspend ? "1" : "0");
        await SetSettingAsync("abuse_outbound_threshold_mbps", outboundThreshold.ToString("0.###", CultureInfo.InvariantCulture));
        await SetSettingAsync("abuse_sustained_threshold_mbps", sustainedThreshold.ToString("0.###", CultureInfo.InvariantCulture));
        await SetSettingAsync("abuse_auto_scan_enabled", config.AutoScanEnabled ? "1" : "0");
        await SetSettingAsync("abuse_scan_interval_seconds", Math.Clamp(config.ScanIntervalSeconds, 10, 3600).ToString(CultureInfo.InvariantCulture));
        await SetSettingAsync("abuse_cooldown_minutes", Math.Clamp(config.CooldownMinutes, 1, 1440).ToString(CultureInfo.InvariantCulture));
        await SetSettingAsync("abuse_required_consecutive_detections", Math.Clamp(config.RequiredConsecutiveDetections, 1, 20).ToString(CultureInfo.InvariantCulture));
        await AddAuditAsync("ABUSE_CONFIG_UPDATED", "settings", "abuse");
    }

    public async Task<PerformanceHistoryConfig> GetPerformanceHistoryConfigAsync()
    {
        var settings = await GetSettingsAsync();
        var defaults = PerformanceHistoryConfig.Default;
        return new PerformanceHistoryConfig(
            settings.GetValueOrDefault("performance_history_enabled", "1") == "1",
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("performance_history_retention_days", "30"), defaults.RetentionDays), 1, 365));
    }

    public async Task SavePerformanceHistoryConfigAsync(PerformanceHistoryConfig config)
    {
        await SetSettingAsync("performance_history_enabled", config.Enabled ? "1" : "0");
        await SetSettingAsync("performance_history_retention_days", Math.Clamp(config.RetentionDays, 1, 365).ToString(CultureInfo.InvariantCulture));
        await AddAuditAsync("PERFORMANCE_HISTORY_CONFIG_UPDATED", "settings", "performance-history", $"Enabled={config.Enabled}; RetentionDays={Math.Clamp(config.RetentionDays, 1, 365)}");
    }

    public async Task StorePerformanceSnapshotAsync(LiveMonitoringOverview snapshot)
    {
        if (!snapshot.HasSample || snapshot.IsStale || snapshot.Host is null) return;

        var sampledAt = new DateTime(snapshot.SampledAt.Year, snapshot.SampledAt.Month, snapshot.SampledAt.Day,
            snapshot.SampledAt.Hour, snapshot.SampledAt.Minute, 0, DateTimeKind.Utc);
        var host = snapshot.Host;
        var memoryPercent = host.MemoryTotalBytes <= 0 ? 0 : host.MemoryUsedBytes * 100d / host.MemoryTotalBytes;
        var diskFreePercent = host.DiskTotalBytes <= 0 ? 0 : host.DiskFreeBytes * 100d / host.DiskTotalBytes;

        await using var db = Open();
        await using var transaction = await db.BeginTransactionAsync();
        await using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = (MySqlTransaction)transaction;
            cmd.CommandText = """
                INSERT INTO host_performance_history
                    (sampled_at, cpu_percent, memory_percent, disk_free_percent, running_vm_count, total_vm_count)
                VALUES (@sampled_at, @cpu, @memory, @disk, @running, @total)
                ON DUPLICATE KEY UPDATE cpu_percent=VALUES(cpu_percent), memory_percent=VALUES(memory_percent),
                    disk_free_percent=VALUES(disk_free_percent), running_vm_count=VALUES(running_vm_count), total_vm_count=VALUES(total_vm_count)
                """;
            cmd.Parameters.AddWithValue("@sampled_at", sampledAt);
            cmd.Parameters.AddWithValue("@cpu", host.CpuPercent);
            cmd.Parameters.AddWithValue("@memory", memoryPercent);
            cmd.Parameters.AddWithValue("@disk", diskFreePercent);
            cmd.Parameters.AddWithValue("@running", host.RunningVmCount);
            cmd.Parameters.AddWithValue("@total", host.RunningVmCount + host.StoppedVmCount);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var item in snapshot.Vms)
        {
            await using var cmd = db.CreateCommand();
            cmd.Transaction = (MySqlTransaction)transaction;
            cmd.CommandText = """
                INSERT INTO vm_performance_history
                    (vm_name, sampled_at, cpu_percent, memory_pressure_percent, network_receive_bps, network_send_bps, disk_read_bps, disk_write_bps, is_running)
                VALUES (@vm_name, @sampled_at, @cpu, @memory, @net_receive, @net_send, @disk_read, @disk_write, @running)
                ON DUPLICATE KEY UPDATE cpu_percent=VALUES(cpu_percent), memory_pressure_percent=VALUES(memory_pressure_percent),
                    network_receive_bps=VALUES(network_receive_bps), network_send_bps=VALUES(network_send_bps),
                    disk_read_bps=VALUES(disk_read_bps), disk_write_bps=VALUES(disk_write_bps), is_running=VALUES(is_running)
                """;
            cmd.Parameters.AddWithValue("@vm_name", item.VmName);
            cmd.Parameters.AddWithValue("@sampled_at", sampledAt);
            cmd.Parameters.AddWithValue("@cpu", item.CpuUsagePercent);
            cmd.Parameters.AddWithValue("@memory", item.MemoryPressurePercent);
            cmd.Parameters.AddWithValue("@net_receive", item.NetworkReceiveBytesPerSecond);
            cmd.Parameters.AddWithValue("@net_send", item.NetworkSendBytesPerSecond);
            cmd.Parameters.AddWithValue("@disk_read", item.DiskReadBytesPerSecond);
            cmd.Parameters.AddWithValue("@disk_write", item.DiskWriteBytesPerSecond);
            cmd.Parameters.AddWithValue("@running", item.IsRunning);
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<HostPerformanceHistoryPoint>> GetHostPerformanceHistoryAsync(int hours, int maxPoints = 360)
    {
        hours = Math.Clamp(hours, 1, 24 * 365);
        var bucketSeconds = Math.Max(60, (int)Math.Ceiling(hours * 3600d / Math.Clamp(maxPoints, 60, 1000)));
        var result = new List<HostPerformanceHistoryPoint>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(sampled_at), AVG(cpu_percent), AVG(memory_percent), AVG(disk_free_percent),
                   ROUND(AVG(running_vm_count)), ROUND(AVG(total_vm_count))
            FROM host_performance_history
            WHERE sampled_at >= @cutoff
            GROUP BY FLOOR(UNIX_TIMESTAMP(sampled_at) / @bucket_seconds)
            ORDER BY MIN(sampled_at)
            """;
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddHours(-hours));
        cmd.Parameters.AddWithValue("@bucket_seconds", bucketSeconds);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new HostPerformanceHistoryPoint(reader.GetDateTime(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetInt32(4), reader.GetInt32(5)));
        }
        return result;
    }

    public async Task<IReadOnlyList<VmPerformanceHistoryPoint>> GetVmPerformanceHistoryAsync(string vmName, int hours, int maxPoints = 360)
    {
        hours = Math.Clamp(hours, 1, 24 * 365);
        var bucketSeconds = Math.Max(60, (int)Math.Ceiling(hours * 3600d / Math.Clamp(maxPoints, 60, 1000)));
        var result = new List<VmPerformanceHistoryPoint>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(sampled_at), AVG(cpu_percent), AVG(memory_pressure_percent), AVG(network_receive_bps),
                   AVG(network_send_bps), AVG(disk_read_bps), AVG(disk_write_bps), MAX(is_running)
            FROM vm_performance_history
            WHERE vm_name = @vm_name AND sampled_at >= @cutoff
            GROUP BY FLOOR(UNIX_TIMESTAMP(sampled_at) / @bucket_seconds)
            ORDER BY MIN(sampled_at)
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddHours(-hours));
        cmd.Parameters.AddWithValue("@bucket_seconds", bucketSeconds);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new VmPerformanceHistoryPoint(reader.GetDateTime(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetBoolean(7)));
        }
        return result;
    }

    public async Task<(long HostSamples, long VmSamples)> GetPerformanceHistoryCountsAsync() =>
        (await ScalarAsync("SELECT COUNT(*) FROM host_performance_history"), await ScalarAsync("SELECT COUNT(*) FROM vm_performance_history"));

    public async Task<(int HostDeleted, int VmDeleted)> CleanupPerformanceHistoryAsync(int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 365));
        var hostCount = await ScalarAsync("SELECT COUNT(*) FROM host_performance_history WHERE sampled_at < @cutoff", ("@cutoff", cutoff));
        var vmCount = await ScalarAsync("SELECT COUNT(*) FROM vm_performance_history WHERE sampled_at < @cutoff", ("@cutoff", cutoff));
        await ExecuteAsync("DELETE FROM host_performance_history WHERE sampled_at < @cutoff", ("@cutoff", cutoff));
        await ExecuteAsync("DELETE FROM vm_performance_history WHERE sampled_at < @cutoff", ("@cutoff", cutoff));
        return ((int)Math.Min(hostCount, int.MaxValue), (int)Math.Min(vmCount, int.MaxValue));
    }

    public async Task<(int HostDeleted, int VmDeleted)> ClearPerformanceHistoryAsync()
    {
        var counts = await GetPerformanceHistoryCountsAsync();
        await ExecuteAsync("DELETE FROM host_performance_history");
        await ExecuteAsync("DELETE FROM vm_performance_history");
        await AddAuditAsync("PERFORMANCE_HISTORY_CLEARED", "performance-history", null, $"Deleted {counts.HostSamples} host and {counts.VmSamples} VPS samples");
        return ((int)Math.Min(counts.HostSamples, int.MaxValue), (int)Math.Min(counts.VmSamples, int.MaxValue));
    }

    public async Task<MonitoringAlertConfig> GetMonitoringAlertConfigAsync()
    {
        var settings = await GetSettingsAsync();
        var defaults = MonitoringAlertConfig.Default;
        return new MonitoringAlertConfig(
            settings.GetValueOrDefault("monitoring_alerts_enabled", "1") == "1",
            settings.GetValueOrDefault("monitoring_alerts_email_enabled", "1") == "1",
            settings.GetValueOrDefault("monitoring_alerts_recovery_emails", "1") == "1",
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("monitoring_alerts_required_samples", "3"), defaults.RequiredConsecutiveSamples), 1, 20),
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("monitoring_alerts_reminder_minutes", "30"), defaults.ReminderMinutes), 5, 1440),
            Math.Clamp(ParseDoubleSetting(settings.GetValueOrDefault("monitoring_alerts_host_cpu_percent", "90"), defaults.HostCpuPercent), 1, 100),
            Math.Clamp(ParseDoubleSetting(settings.GetValueOrDefault("monitoring_alerts_host_memory_percent", "90"), defaults.HostMemoryPercent), 1, 100),
            Math.Clamp(ParseDoubleSetting(settings.GetValueOrDefault("monitoring_alerts_host_disk_free_percent", "10"), defaults.HostDiskFreePercent), 1, 99),
            Math.Clamp(ParseDoubleSetting(settings.GetValueOrDefault("monitoring_alerts_vm_cpu_percent", "90"), defaults.VmCpuPercent), 1, 100),
            Math.Clamp(ParseDoubleSetting(settings.GetValueOrDefault("monitoring_alerts_vm_memory_percent", "100"), defaults.VmMemoryPressurePercent), 1, 500),
            settings.GetValueOrDefault("monitoring_alerts_heartbeat", "1") == "1",
            settings.GetValueOrDefault("monitoring_alerts_adapters", "1") == "1");
    }

    public async Task SaveMonitoringAlertConfigAsync(MonitoringAlertConfig config)
    {
        await SetSettingAsync("monitoring_alerts_enabled", config.Enabled ? "1" : "0");
        await SetSettingAsync("monitoring_alerts_email_enabled", config.EmailEnabled ? "1" : "0");
        await SetSettingAsync("monitoring_alerts_recovery_emails", config.RecoveryEmails ? "1" : "0");
        await SetSettingAsync("monitoring_alerts_required_samples", Math.Clamp(config.RequiredConsecutiveSamples, 1, 20).ToString(CultureInfo.InvariantCulture));
        await SetSettingAsync("monitoring_alerts_reminder_minutes", Math.Clamp(config.ReminderMinutes, 5, 1440).ToString(CultureInfo.InvariantCulture));
        await SetSettingAsync("monitoring_alerts_host_cpu_percent", Math.Clamp(config.HostCpuPercent, 1, 100).ToString("0.#", CultureInfo.InvariantCulture));
        await SetSettingAsync("monitoring_alerts_host_memory_percent", Math.Clamp(config.HostMemoryPercent, 1, 100).ToString("0.#", CultureInfo.InvariantCulture));
        await SetSettingAsync("monitoring_alerts_host_disk_free_percent", Math.Clamp(config.HostDiskFreePercent, 1, 99).ToString("0.#", CultureInfo.InvariantCulture));
        await SetSettingAsync("monitoring_alerts_vm_cpu_percent", Math.Clamp(config.VmCpuPercent, 1, 100).ToString("0.#", CultureInfo.InvariantCulture));
        await SetSettingAsync("monitoring_alerts_vm_memory_percent", Math.Clamp(config.VmMemoryPressurePercent, 1, 500).ToString("0.#", CultureInfo.InvariantCulture));
        await SetSettingAsync("monitoring_alerts_heartbeat", config.HeartbeatAlerts ? "1" : "0");
        await SetSettingAsync("monitoring_alerts_adapters", config.AdapterAlerts ? "1" : "0");
        await AddAuditAsync("MONITORING_ALERT_CONFIG_UPDATED", "settings", "monitoring-alerts");
    }

    public async Task<long> CreateMonitoringAlertAsync(
        string alertKey,
        string scopeType,
        string scopeName,
        string alertType,
        string severity,
        string title,
        string message,
        double? currentValue,
        double? thresholdValue)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO monitoring_alerts
                (alert_key, scope_type, scope_name, alert_type, severity, title, message, current_value, threshold_value)
            VALUES
                (@alert_key, @scope_type, @scope_name, @alert_type, @severity, @title, @message, @current_value, @threshold_value)
            """;
        cmd.Parameters.AddWithValue("@alert_key", alertKey);
        cmd.Parameters.AddWithValue("@scope_type", scopeType);
        cmd.Parameters.AddWithValue("@scope_name", scopeName);
        cmd.Parameters.AddWithValue("@alert_type", alertType);
        cmd.Parameters.AddWithValue("@severity", severity);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@message", message);
        cmd.Parameters.AddWithValue("@current_value", (object?)currentValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@threshold_value", (object?)thresholdValue ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return cmd.LastInsertedId;
    }

    public async Task TouchMonitoringAlertAsync(long id, string message, double? currentValue, double? thresholdValue)
    {
        await ExecuteAsync("""
            UPDATE monitoring_alerts
            SET message = @message,
                current_value = @current_value,
                threshold_value = @threshold_value,
                occurrence_count = occurrence_count + 1,
                last_triggered_at = @now
            WHERE id = @id AND status IN ('active', 'acknowledged')
            """,
            ("@id", id),
            ("@message", message),
            ("@current_value", currentValue),
            ("@threshold_value", thresholdValue),
            ("@now", DateTime.UtcNow));
    }

    public Task MarkMonitoringAlertNotifiedAsync(long id) => ExecuteAsync(
        "UPDATE monitoring_alerts SET last_notified_at = @now WHERE id = @id",
        ("@id", id), ("@now", DateTime.UtcNow));

    public async Task AcknowledgeMonitoringAlertAsync(long id, string username)
    {
        await ExecuteAsync("""
            UPDATE monitoring_alerts
            SET status = 'acknowledged', acknowledged_at = @now, acknowledged_by = @username
            WHERE id = @id AND status = 'active'
            """, ("@id", id), ("@now", DateTime.UtcNow), ("@username", username));
        await AddAuditAsync("MONITORING_ALERT_ACKNOWLEDGED", "monitoring-alert", id.ToString(CultureInfo.InvariantCulture), $"By {username}");
    }

    public async Task ResolveMonitoringAlertAsync(long id, string resolutionMessage)
    {
        await ExecuteAsync("""
            UPDATE monitoring_alerts
            SET status = 'resolved', resolved_at = @now, resolution_message = @message
            WHERE id = @id AND status IN ('active', 'acknowledged')
            """, ("@id", id), ("@now", DateTime.UtcNow), ("@message", resolutionMessage));
    }

    public async Task<IReadOnlyList<MonitoringAlert>> GetOpenMonitoringAlertsAsync() =>
        await ReadMonitoringAlertsAsync(true, 500);

    public async Task<IReadOnlyList<MonitoringAlert>> GetMonitoringAlertsAsync(int limit = 200) =>
        await ReadMonitoringAlertsAsync(false, Math.Clamp(limit, 1, 1000));

    public async Task<int> ClearResolvedMonitoringAlertsAsync()
    {
        var count = await ScalarAsync("SELECT COUNT(*) FROM monitoring_alerts WHERE status = 'resolved'");
        await ExecuteAsync("DELETE FROM monitoring_alerts WHERE status = 'resolved'");
        await AddAuditAsync("MONITORING_ALERT_HISTORY_CLEARED", "monitoring-alert", null, $"Deleted {count} resolved alert(s)");
        return (int)Math.Min(count, int.MaxValue);
    }

    private async Task<IReadOnlyList<MonitoringAlert>> ReadMonitoringAlertsAsync(bool openOnly, int limit)
    {
        var alerts = new List<MonitoringAlert>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, alert_key, scope_type, scope_name, alert_type, severity, title, message,
                   current_value, threshold_value, status, occurrence_count, first_triggered_at,
                   last_triggered_at, last_notified_at, acknowledged_at, acknowledged_by,
                   resolved_at, resolution_message
            FROM monitoring_alerts
            {(openOnly ? "WHERE status IN ('active', 'acknowledged')" : string.Empty)}
            ORDER BY CASE status WHEN 'active' THEN 0 WHEN 'acknowledged' THEN 1 ELSE 2 END,
                     last_triggered_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            alerts.Add(new MonitoringAlert(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetDouble(9),
                reader.GetString(10), reader.GetInt32(11), ReadDate(reader, 12), ReadDate(reader, 13),
                reader.IsDBNull(14) ? null : ReadDate(reader, 14),
                reader.IsDBNull(15) ? null : ReadDate(reader, 15),
                reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
                reader.IsDBNull(17) ? null : ReadDate(reader, 17),
                reader.IsDBNull(18) ? string.Empty : reader.GetString(18)));
        }

        return alerts;
    }

    public async Task<VmAvailabilityConfig> GetVmAvailabilityConfigAsync()
    {
        var settings = await GetSettingsAsync();
        var defaults = VmAvailabilityConfig.Default;
        return new VmAvailabilityConfig(
            settings.GetValueOrDefault("availability_enabled", "1") == "1",
            settings.GetValueOrDefault("availability_client_email_enabled", "1") == "1",
            settings.GetValueOrDefault("availability_notify_admins", "1") == "1",
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("availability_unexpected_down_minutes", "1"), defaults.UnexpectedDownMinutes), 1, 1440),
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("availability_guest_unresponsive_minutes", "10"), defaults.GuestUnresponsiveMinutes), 1, 1440),
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("availability_extended_downtime_minutes", "60"), defaults.ExtendedDowntimeMinutes), 15, 43200),
            settings.GetValueOrDefault("availability_recovery_notifications", "1") == "1");
    }

    public async Task SaveVmAvailabilityConfigAsync(VmAvailabilityConfig config)
    {
        await SetSettingAsync("availability_enabled", config.Enabled ? "1" : "0");
        await SetSettingAsync("availability_client_email_enabled", config.ClientEmailEnabled ? "1" : "0");
        await SetSettingAsync("availability_notify_admins", config.NotifyAdmins ? "1" : "0");
        await SetSettingAsync("availability_unexpected_down_minutes", Math.Clamp(config.UnexpectedDownMinutes, 1, 1440).ToString(CultureInfo.InvariantCulture));
        await SetSettingAsync("availability_guest_unresponsive_minutes", Math.Clamp(config.GuestUnresponsiveMinutes, 1, 1440).ToString(CultureInfo.InvariantCulture));
        await SetSettingAsync("availability_extended_downtime_minutes", Math.Clamp(config.ExtendedDowntimeMinutes, 15, 43200).ToString(CultureInfo.InvariantCulture));
        await SetSettingAsync("availability_recovery_notifications", config.RecoveryNotifications ? "1" : "0");
        await AddAuditAsync("VM_AVAILABILITY_CONFIG_UPDATED", "settings", "availability");
    }

    public async Task<IReadOnlyList<VmAvailabilityState>> GetVmAvailabilityStatesAsync()
    {
        var items = new List<VmAvailabilityState>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, last_state, last_heartbeat_healthy, was_observed_running, down_was_planned, last_sample_at,
                   down_since, unresponsive_since, planned_until, planned_reason,
                   unexpected_notified_at, extended_notified_at, unresponsive_notified_at, updated_at
            FROM vm_availability_state
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(ReadVmAvailabilityState(reader));
        }
        return items;
    }

    public async Task<VmAvailabilityState?> GetVmAvailabilityStateAsync(string vmName)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, last_state, last_heartbeat_healthy, was_observed_running, down_was_planned, last_sample_at,
                   down_since, unresponsive_since, planned_until, planned_reason,
                   unexpected_notified_at, extended_notified_at, unresponsive_notified_at, updated_at
            FROM vm_availability_state
            WHERE vm_name = @vm_name
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadVmAvailabilityState(reader) : null;
    }

    public async Task SaveVmAvailabilityStateAsync(VmAvailabilityState state)
    {
        await ExecuteAsync("""
            INSERT INTO vm_availability_state
                (vm_name, last_state, last_heartbeat_healthy, was_observed_running, down_was_planned, last_sample_at,
                 down_since, unresponsive_since, planned_until, planned_reason,
                 unexpected_notified_at, extended_notified_at, unresponsive_notified_at, updated_at)
            VALUES
                (@vm_name, @last_state, @heartbeat, @observed_running, @down_was_planned, @last_sample_at,
                 @down_since, @unresponsive_since, @planned_until, @planned_reason,
                 @unexpected_notified_at, @extended_notified_at, @unresponsive_notified_at, @updated_at)
            ON DUPLICATE KEY UPDATE
                last_state = VALUES(last_state),
                last_heartbeat_healthy = VALUES(last_heartbeat_healthy),
                was_observed_running = VALUES(was_observed_running),
                down_was_planned = VALUES(down_was_planned),
                last_sample_at = VALUES(last_sample_at),
                down_since = VALUES(down_since),
                unresponsive_since = VALUES(unresponsive_since),
                planned_until = VALUES(planned_until),
                planned_reason = VALUES(planned_reason),
                unexpected_notified_at = VALUES(unexpected_notified_at),
                extended_notified_at = VALUES(extended_notified_at),
                unresponsive_notified_at = VALUES(unresponsive_notified_at),
                updated_at = VALUES(updated_at)
            """,
            ("@vm_name", state.VmName), ("@last_state", state.LastState),
            ("@heartbeat", ToInt(state.LastHeartbeatHealthy)), ("@observed_running", ToInt(state.WasObservedRunning)),
            ("@down_was_planned", ToInt(state.DownWasPlanned)),
            ("@last_sample_at", state.LastSampleAt), ("@down_since", state.DownSince),
            ("@unresponsive_since", state.UnresponsiveSince), ("@planned_until", state.PlannedUntil),
            ("@planned_reason", state.PlannedReason), ("@unexpected_notified_at", state.UnexpectedNotifiedAt),
            ("@extended_notified_at", state.ExtendedNotifiedAt), ("@unresponsive_notified_at", state.UnresponsiveNotifiedAt),
            ("@updated_at", state.UpdatedAt));
    }

    public async Task MarkVmPlannedDowntimeAsync(string vmName, DateTime plannedUntil, string reason)
    {
        await ExecuteAsync("""
            INSERT INTO vm_availability_state
                (vm_name, last_state, last_heartbeat_healthy, was_observed_running, last_sample_at, planned_until, planned_reason)
            VALUES (@vm_name, 'Unknown', 0, 0, @now, @planned_until, @reason)
            ON DUPLICATE KEY UPDATE planned_until = VALUES(planned_until), planned_reason = VALUES(planned_reason), updated_at = @now
            """, ("@vm_name", vmName), ("@now", DateTime.UtcNow), ("@planned_until", plannedUntil), ("@reason", reason));
        await AddAuditAsync("VM_AVAILABILITY_MAINTENANCE_SET", "vm", vmName, $"Until {plannedUntil:u}: {reason}");
    }

    public async Task ClearVmPlannedDowntimeAsync(string vmName, string username)
    {
        await ExecuteAsync("UPDATE vm_availability_state SET planned_until = NULL, planned_reason = '', updated_at = @now WHERE vm_name = @vm_name",
            ("@vm_name", vmName), ("@now", DateTime.UtcNow));
        await AddAuditAsync("VM_AVAILABILITY_MAINTENANCE_CLEARED", "vm", vmName, $"By {username}");
    }

    public async Task<long> AddVmAvailabilityEventAsync(string vmName, string eventType, string severity, string title, string message, DateTime startedAt)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vm_availability_events (vm_name, event_type, severity, title, message, started_at)
            VALUES (@vm_name, @event_type, @severity, @title, @message, @started_at)
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        cmd.Parameters.AddWithValue("@event_type", eventType);
        cmd.Parameters.AddWithValue("@severity", severity);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@message", message);
        cmd.Parameters.AddWithValue("@started_at", startedAt);
        await cmd.ExecuteNonQueryAsync();
        return cmd.LastInsertedId;
    }

    public async Task<IReadOnlyList<VmAvailabilityEvent>> GetVmAvailabilityEventsAsync(string vmName, int limit = 50)
    {
        var items = new List<VmAvailabilityEvent>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, vm_name, event_type, severity, title, message, started_at, created_at
            FROM vm_availability_events
            WHERE vm_name = @vm_name
            ORDER BY id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new VmAvailabilityEvent(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), ReadDate(reader, 6), ReadDate(reader, 7)));
        }
        return items;
    }

    public async Task<VmAvailabilityPreference> GetVmAvailabilityPreferenceAsync(long userId, string vmName)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT is_enabled, notify_unexpected_down, notify_extended_downtime,
                   notify_guest_unresponsive, notify_recovery
            FROM vm_availability_preferences
            WHERE user_id = @user_id AND vm_name = @vm_name
            """;
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return VmAvailabilityPreference.Default(userId, vmName);
        return new VmAvailabilityPreference(userId, vmName, ReadBool(reader, 0), ReadBool(reader, 1), ReadBool(reader, 2), ReadBool(reader, 3), ReadBool(reader, 4));
    }

    public async Task SaveVmAvailabilityPreferenceAsync(VmAvailabilityPreference preference)
    {
        await ExecuteAsync("""
            INSERT INTO vm_availability_preferences
                (user_id, vm_name, is_enabled, notify_unexpected_down, notify_extended_downtime, notify_guest_unresponsive, notify_recovery)
            VALUES (@user_id, @vm_name, @enabled, @unexpected, @extended, @unresponsive, @recovery)
            ON DUPLICATE KEY UPDATE
                is_enabled = VALUES(is_enabled), notify_unexpected_down = VALUES(notify_unexpected_down),
                notify_extended_downtime = VALUES(notify_extended_downtime),
                notify_guest_unresponsive = VALUES(notify_guest_unresponsive), notify_recovery = VALUES(notify_recovery),
                updated_at = CURRENT_TIMESTAMP(6)
            """,
            ("@user_id", preference.UserId), ("@vm_name", preference.VmName), ("@enabled", ToInt(preference.Enabled)),
            ("@unexpected", ToInt(preference.NotifyUnexpectedDown)), ("@extended", ToInt(preference.NotifyExtendedDowntime)),
            ("@unresponsive", ToInt(preference.NotifyGuestUnresponsive)), ("@recovery", ToInt(preference.NotifyRecovery)));
    }

    public async Task<IReadOnlyList<VmAvailabilityRecipient>> GetVmAvailabilityRecipientsAsync(string vmName)
    {
        var recipients = new List<VmAvailabilityRecipient>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT u.id, u.username, u.email,
                   COALESCE(p.is_enabled, 1), COALESCE(p.notify_unexpected_down, 1),
                   COALESCE(p.notify_extended_downtime, 1), COALESCE(p.notify_guest_unresponsive, 1),
                   COALESCE(p.notify_recovery, 1)
            FROM (
                SELECT a.user_id
                FROM user_vm_assignments a
                WHERE lower(a.vm_name) = lower(@vm_name)

                UNION

                SELECT s.panel_user_id
                FROM whmcs_services s
                WHERE lower(s.vm_name) = lower(@vm_name)
                  AND lower(COALESCE(s.status, '')) <> 'terminated'
            ) owners
            INNER JOIN users u ON u.id = owners.user_id
            LEFT JOIN vm_availability_preferences p
                ON p.user_id = u.id AND lower(p.vm_name) = lower(@vm_name)
            WHERE lower(u.role) <> 'admin'
              AND u.email IS NOT NULL
              AND trim(u.email) <> ''
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var userId = reader.GetInt64(0);
            recipients.Add(new VmAvailabilityRecipient(
                userId, reader.GetString(1), reader.GetString(2),
                new VmAvailabilityPreference(userId, vmName, ReadBool(reader, 3), ReadBool(reader, 4), ReadBool(reader, 5), ReadBool(reader, 6), ReadBool(reader, 7))));
        }
        return recipients;
    }

    private static VmAvailabilityState ReadVmAvailabilityState(MySqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), ReadBool(reader, 2), ReadBool(reader, 3), ReadBool(reader, 4), ReadDate(reader, 5),
        reader.IsDBNull(6) ? null : ReadDate(reader, 6), reader.IsDBNull(7) ? null : ReadDate(reader, 7),
        reader.IsDBNull(8) ? null : ReadDate(reader, 8), reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
        reader.IsDBNull(10) ? null : ReadDate(reader, 10), reader.IsDBNull(11) ? null : ReadDate(reader, 11),
        reader.IsDBNull(12) ? null : ReadDate(reader, 12), ReadDate(reader, 13));

    public async Task<VmSelfHealingConfig> GetVmSelfHealingConfigAsync()
    {
        var settings = await GetSettingsAsync();
        var defaults = VmSelfHealingConfig.Default;
        return new VmSelfHealingConfig(
            settings.GetValueOrDefault("self_healing_enabled", "0") == "1",
            settings.GetValueOrDefault("self_healing_client_email_enabled", "1") == "1",
            settings.GetValueOrDefault("self_healing_notify_admins", "1") == "1",
            settings.GetValueOrDefault("self_healing_unexpected_stop_recovery", "1") == "1",
            settings.GetValueOrDefault("self_healing_heartbeat_recovery", "0") == "1",
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("self_healing_unexpected_stop_delay_minutes", "3"), defaults.UnexpectedStopDelayMinutes), 1, 1440),
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("self_healing_heartbeat_delay_minutes", "20"), defaults.HeartbeatDelayMinutes), 5, 1440),
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("self_healing_confirmation_minutes", "3"), defaults.ConfirmationMinutes), 1, 30),
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("self_healing_cooldown_minutes", "30"), defaults.CooldownMinutes), 5, 1440),
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("self_healing_max_attempts", "2"), defaults.MaxAttempts), 1, 5),
            Math.Clamp(ParseIntSetting(settings.GetValueOrDefault("self_healing_attempt_window_minutes", "360"), defaults.AttemptWindowMinutes), 60, 10080));
    }

    public async Task SaveVmSelfHealingConfigAsync(VmSelfHealingConfig config)
    {
        await SetSettingAsync("self_healing_enabled", config.Enabled ? "1" : "0");
        await SetSettingAsync("self_healing_client_email_enabled", config.ClientEmailEnabled ? "1" : "0");
        await SetSettingAsync("self_healing_notify_admins", config.NotifyAdmins ? "1" : "0");
        await SetSettingAsync("self_healing_unexpected_stop_recovery", config.UnexpectedStopRecovery ? "1" : "0");
        await SetSettingAsync("self_healing_heartbeat_recovery", config.HeartbeatRecovery ? "1" : "0");
        await SetSettingAsync("self_healing_unexpected_stop_delay_minutes", Math.Clamp(config.UnexpectedStopDelayMinutes, 1, 1440).ToString(CultureInfo.InvariantCulture));
        await SetSettingAsync("self_healing_heartbeat_delay_minutes", Math.Clamp(config.HeartbeatDelayMinutes, 5, 1440).ToString(CultureInfo.InvariantCulture));
        await SetSettingAsync("self_healing_confirmation_minutes", Math.Clamp(config.ConfirmationMinutes, 1, 30).ToString(CultureInfo.InvariantCulture));
        await SetSettingAsync("self_healing_cooldown_minutes", Math.Clamp(config.CooldownMinutes, 5, 1440).ToString(CultureInfo.InvariantCulture));
        await SetSettingAsync("self_healing_max_attempts", Math.Clamp(config.MaxAttempts, 1, 5).ToString(CultureInfo.InvariantCulture));
        await SetSettingAsync("self_healing_attempt_window_minutes", Math.Clamp(config.AttemptWindowMinutes, 60, 10080).ToString(CultureInfo.InvariantCulture));
        await AddAuditAsync("VM_SELF_HEALING_CONFIG_UPDATED", "settings", "self-healing");
    }

    public async Task<VmSelfHealingPreference> GetVmSelfHealingPreferenceAsync(string vmName)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT is_enabled, recover_unexpected_stop, recover_unresponsive,
                   hold_until, hold_reason, updated_at
            FROM vm_self_healing_preferences
            WHERE vm_name = @vm_name
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return VmSelfHealingPreference.Default(vmName);
        return ReadVmSelfHealingPreference(reader, vmName);
    }

    public async Task<IReadOnlyList<VmSelfHealingPreference>> GetEnabledVmSelfHealingPreferencesAsync()
    {
        var items = new List<VmSelfHealingPreference>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, is_enabled, recover_unexpected_stop, recover_unresponsive,
                   hold_until, hold_reason, updated_at
            FROM vm_self_healing_preferences
            WHERE is_enabled = 1
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new VmSelfHealingPreference(
                reader.GetString(0), ReadBool(reader, 1), ReadBool(reader, 2), ReadBool(reader, 3),
                reader.IsDBNull(4) ? null : ReadDate(reader, 4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5), ReadDate(reader, 6)));
        }
        return items;
    }

    public async Task SaveVmSelfHealingPreferenceAsync(VmSelfHealingPreference preference)
    {
        await ExecuteAsync("""
            INSERT INTO vm_self_healing_preferences
                (vm_name, is_enabled, recover_unexpected_stop, recover_unresponsive, hold_until, hold_reason, updated_at)
            VALUES (@vm_name, @enabled, @unexpected, @unresponsive, @hold_until, @hold_reason, @updated_at)
            ON DUPLICATE KEY UPDATE
                is_enabled = VALUES(is_enabled), recover_unexpected_stop = VALUES(recover_unexpected_stop),
                recover_unresponsive = VALUES(recover_unresponsive), hold_until = VALUES(hold_until),
                hold_reason = VALUES(hold_reason), updated_at = VALUES(updated_at)
            """,
            ("@vm_name", preference.VmName), ("@enabled", ToInt(preference.Enabled)),
            ("@unexpected", ToInt(preference.RecoverUnexpectedStop)), ("@unresponsive", ToInt(preference.RecoverUnresponsive)),
            ("@hold_until", preference.HoldUntil), ("@hold_reason", preference.HoldReason ?? string.Empty),
            ("@updated_at", DateTime.UtcNow));
    }

    public async Task SetVmSelfHealingHoldAsync(string vmName, DateTime? holdUntil, string reason)
    {
        await ExecuteAsync("""
            INSERT INTO vm_self_healing_preferences
                (vm_name, is_enabled, recover_unexpected_stop, recover_unresponsive, hold_until, hold_reason)
            VALUES (@vm_name, 0, 1, 0, @hold_until, @reason)
            ON DUPLICATE KEY UPDATE hold_until = VALUES(hold_until), hold_reason = VALUES(hold_reason), updated_at = CURRENT_TIMESTAMP(6)
            """, ("@vm_name", vmName), ("@hold_until", holdUntil), ("@reason", reason));
        await AddAuditAsync("VM_SELF_HEALING_HOLD_SET", "vm", vmName, holdUntil is null ? reason : $"Until {holdUntil:u}: {reason}");
    }

    public async Task ClearVmSelfHealingHoldAsync(string vmName, string actor)
    {
        await ExecuteAsync("UPDATE vm_self_healing_preferences SET hold_until = NULL, hold_reason = '', updated_at = @now WHERE vm_name = @vm_name",
            ("@vm_name", vmName), ("@now", DateTime.UtcNow));
        await AddAuditAsync("VM_SELF_HEALING_HOLD_CLEARED", "vm", vmName, $"By {actor}");
    }

    public async Task<IReadOnlyDictionary<string, VmSelfHealingState>> GetVmSelfHealingStatesAsync()
    {
        var items = new Dictionary<string, VmSelfHealingState>(StringComparer.OrdinalIgnoreCase);
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, incident_key, incident_type, incident_started_at, attempt_count,
                   window_started_at, last_attempt_at, next_attempt_at, status, last_result, updated_at
            FROM vm_self_healing_state
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var item = ReadVmSelfHealingState(reader);
            items[item.VmName] = item;
        }
        return items;
    }

    public async Task<VmSelfHealingState?> GetVmSelfHealingStateAsync(string vmName)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, incident_key, incident_type, incident_started_at, attempt_count,
                   window_started_at, last_attempt_at, next_attempt_at, status, last_result, updated_at
            FROM vm_self_healing_state
            WHERE vm_name = @vm_name
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadVmSelfHealingState(reader) : null;
    }

    public async Task SaveVmSelfHealingStateAsync(VmSelfHealingState state)
    {
        await ExecuteAsync("""
            INSERT INTO vm_self_healing_state
                (vm_name, incident_key, incident_type, incident_started_at, attempt_count, window_started_at,
                 last_attempt_at, next_attempt_at, status, last_result, updated_at)
            VALUES (@vm_name, @incident_key, @incident_type, @incident_started_at, @attempt_count, @window_started_at,
                    @last_attempt_at, @next_attempt_at, @status, @last_result, @updated_at)
            ON DUPLICATE KEY UPDATE incident_key = VALUES(incident_key), incident_type = VALUES(incident_type),
                incident_started_at = VALUES(incident_started_at), attempt_count = VALUES(attempt_count),
                window_started_at = VALUES(window_started_at), last_attempt_at = VALUES(last_attempt_at),
                next_attempt_at = VALUES(next_attempt_at), status = VALUES(status),
                last_result = VALUES(last_result), updated_at = VALUES(updated_at)
            """,
            ("@vm_name", state.VmName), ("@incident_key", state.IncidentKey), ("@incident_type", state.IncidentType),
            ("@incident_started_at", state.IncidentStartedAt), ("@attempt_count", state.AttemptCount),
            ("@window_started_at", state.WindowStartedAt), ("@last_attempt_at", state.LastAttemptAt),
            ("@next_attempt_at", state.NextAttemptAt), ("@status", state.Status),
            ("@last_result", state.LastResult), ("@updated_at", state.UpdatedAt));
    }

    public async Task ResetVmSelfHealingStateAsync(string vmName, string actor)
    {
        await ExecuteAsync("""
            INSERT INTO vm_self_healing_state
                (vm_name, incident_key, incident_type, attempt_count, status, last_result, updated_at)
            VALUES (@vm_name, '', '', 0, 'idle', @result, @now)
            ON DUPLICATE KEY UPDATE incident_key = '', incident_type = '', incident_started_at = NULL,
                attempt_count = 0, window_started_at = NULL, last_attempt_at = NULL,
                next_attempt_at = NULL, status = 'idle', last_result = VALUES(last_result), updated_at = VALUES(updated_at)
            """, ("@vm_name", vmName), ("@result", $"Reset by {actor}."), ("@now", DateTime.UtcNow));
        await AddAuditAsync("VM_SELF_HEALING_STATE_RESET", "vm", vmName, $"By {actor}");
    }

    public async Task<long> AddVmSelfHealingEventAsync(string vmName, string incidentType, string eventType, string status, int attemptNumber, string message)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vm_self_healing_events (vm_name, incident_type, event_type, status, attempt_number, message)
            VALUES (@vm_name, @incident_type, @event_type, @status, @attempt_number, @message)
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        cmd.Parameters.AddWithValue("@incident_type", incidentType);
        cmd.Parameters.AddWithValue("@event_type", eventType);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@attempt_number", attemptNumber);
        cmd.Parameters.AddWithValue("@message", message);
        await cmd.ExecuteNonQueryAsync();
        return cmd.LastInsertedId;
    }

    public async Task<IReadOnlyList<VmSelfHealingEvent>> GetVmSelfHealingEventsAsync(string vmName, int limit = 20)
    {
        var items = new List<VmSelfHealingEvent>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, vm_name, incident_type, event_type, status, attempt_number, message, created_at
            FROM vm_self_healing_events
            WHERE vm_name = @vm_name
            ORDER BY id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new VmSelfHealingEvent(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetString(6), ReadDate(reader, 7)));
        }
        return items;
    }

    private static VmSelfHealingPreference ReadVmSelfHealingPreference(MySqlDataReader reader, string vmName) => new(
        vmName, ReadBool(reader, 0), ReadBool(reader, 1), ReadBool(reader, 2),
        reader.IsDBNull(3) ? null : ReadDate(reader, 3),
        reader.IsDBNull(4) ? string.Empty : reader.GetString(4), ReadDate(reader, 5));

    private static VmSelfHealingState ReadVmSelfHealingState(MySqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : ReadDate(reader, 3), reader.GetInt32(4),
        reader.IsDBNull(5) ? null : ReadDate(reader, 5), reader.IsDBNull(6) ? null : ReadDate(reader, 6),
        reader.IsDBNull(7) ? null : ReadDate(reader, 7), reader.GetString(8),
        reader.IsDBNull(9) ? string.Empty : reader.GetString(9), ReadDate(reader, 10));

    public async Task AddBandwidthSampleAsync(string vmName, long bytesIn, long bytesOut)
    {
        await ExecuteAsync("""
            INSERT INTO bandwidth_history (vm_name, bytes_in, bytes_out)
            VALUES (@vm_name, @bytes_in, @bytes_out)
            """,
            ("@vm_name", vmName),
            ("@bytes_in", bytesIn),
            ("@bytes_out", bytesOut));
    }

    public async Task<(long BytesOut, DateTime CreatedAt)?> GetPreviousBandwidthSampleAsync(string vmName)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT bytes_out, created_at
            FROM bandwidth_history
            WHERE vm_name = @vm_name
            ORDER BY id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return (reader.GetInt64(0), ReadDate(reader, 1));
    }

    public async Task AddAbuseEventAsync(string vmName, string severity, double outboundMbps, string actionTaken)
    {
        await ExecuteAsync("""
            INSERT INTO abuse_events (vm_name, detection_type, severity, outbound_mbps, action_taken)
            VALUES (@vm_name, 'outbound_abuse', @severity, @outbound_mbps, @action_taken)
            """,
            ("@vm_name", vmName),
            ("@severity", severity),
            ("@outbound_mbps", outboundMbps),
            ("@action_taken", actionTaken));
        await AddAuditAsync("OUTBOUND_ABUSE_DETECTED", "vm", vmName, $"{severity}: {outboundMbps:0.###} Mbps");
    }


    public async Task DeleteAbuseEventAsync(long eventId)
    {
        await ExecuteAsync("DELETE FROM abuse_events WHERE id = @id", ("@id", eventId));
        await AddAuditAsync("ABUSE_EVENT_DELETED", "abuse_event", eventId.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<int> ClearAbuseEventsAsync()
    {
        var count = await ScalarAsync("SELECT COUNT(*) FROM abuse_events");
        await ExecuteAsync("DELETE FROM abuse_events");
        await AddAuditAsync("ABUSE_EVENTS_CLEARED", "abuse_event", null, $"Deleted {count} event(s)");
        return (int)Math.Min(count, int.MaxValue);
    }

    public async Task<(int EventsDeleted, int SamplesDeleted)> DeleteOldAbuseDataAsync(int olderThanDays)
    {
        olderThanDays = Math.Clamp(olderThanDays, 1, 3650);
        var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);

        var eventsCount = await ScalarAsync("SELECT COUNT(*) FROM abuse_events WHERE created_at < @cutoff", ("@cutoff", cutoff));
        var samplesCount = await ScalarAsync("SELECT COUNT(*) FROM bandwidth_history WHERE created_at < @cutoff", ("@cutoff", cutoff));

        await ExecuteAsync("DELETE FROM abuse_events WHERE created_at < @cutoff", ("@cutoff", cutoff));
        await ExecuteAsync("DELETE FROM bandwidth_history WHERE created_at < @cutoff", ("@cutoff", cutoff));

        await AddAuditAsync("ABUSE_DATA_PRUNED", "abuse_event", null, $"Deleted {eventsCount} event(s) and {samplesCount} meter sample(s) older than {olderThanDays} day(s)");
        return ((int)Math.Min(eventsCount, int.MaxValue), (int)Math.Min(samplesCount, int.MaxValue));
    }

    public async Task<IReadOnlyList<AbuseEvent>> GetAbuseEventsAsync(int limit = 100)
    {
        var events = new List<AbuseEvent>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, vm_name, detection_type, severity, outbound_mbps, action_taken, created_at
            FROM abuse_events
            ORDER BY id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add(new AbuseEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDouble(4),
                reader.GetString(5),
                ReadDate(reader, 6)));
        }

        return events;
    }

    private async Task<DateTime?> GetLoginLockoutUntilAsync(string username)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT locked_until FROM login_attempts WHERE username = @username LIMIT 1";
        cmd.Parameters.AddWithValue("@username", username);
        var value = await cmd.ExecuteScalarAsync();
        return value is null || value == DBNull.Value ? null : Convert.ToDateTime(value, CultureInfo.InvariantCulture);
    }

    private async Task RegisterFailedLoginAsync(string username)
    {
        var maxAttempts = Math.Clamp(configuration.GetValue<int?>("Security:MaxFailedLoginAttempts") ?? 5, 2, 50);
        var lockoutMinutes = Math.Clamp(configuration.GetValue<int?>("Security:LoginLockoutMinutes") ?? 15, 1, 1440);

        await ExecuteAsync("""
            INSERT INTO login_attempts (username, failed_count, locked_until, last_failed_at)
            VALUES (@username, 1, NULL, @now)
            ON DUPLICATE KEY UPDATE
                failed_count = failed_count + 1,
                locked_until = CASE WHEN failed_count + 1 >= @max_attempts THEN @locked_until ELSE locked_until END,
                last_failed_at = @now
            """,
            ("@username", username),
            ("@now", DateTime.UtcNow),
            ("@max_attempts", maxAttempts),
            ("@locked_until", DateTime.UtcNow.AddMinutes(lockoutMinutes)));
    }

    private Task ClearFailedLoginAsync(string username) =>
        ExecuteAsync("DELETE FROM login_attempts WHERE username = @username", ("@username", username));

    public async Task<UserResourceLimits> GetUserResourceLimitsAsync(long userId)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, max_vm_count, max_cpu_cores, max_memory_mb, max_disk_gb, max_power_actions_per_hour
            FROM user_resource_limits
            WHERE user_id = @user_id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@user_id", userId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return UserResourceLimits.Default(userId);
        }

        return new UserResourceLimits(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            reader.IsDBNull(5) ? 20 : reader.GetInt32(5));
    }

    public async Task<Dictionary<long, UserResourceLimits>> GetUserResourceLimitsAsync()
    {
        var result = new Dictionary<long, UserResourceLimits>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT user_id, max_vm_count, max_cpu_cores, max_memory_mb, max_disk_gb, max_power_actions_per_hour FROM user_resource_limits";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var limits = new UserResourceLimits(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                reader.IsDBNull(5) ? 20 : reader.GetInt32(5));
            result[limits.UserId] = limits;
        }

        return result;
    }

    public async Task SaveUserResourceLimitsAsync(UserResourceLimits limits)
    {
        await ExecuteAsync("""
            INSERT INTO user_resource_limits (user_id, max_vm_count, max_cpu_cores, max_memory_mb, max_disk_gb, max_power_actions_per_hour, updated_at)
            VALUES (@user_id, @max_vm_count, @max_cpu_cores, @max_memory_mb, @max_disk_gb, @max_power_actions_per_hour, @updated_at)
            ON DUPLICATE KEY UPDATE
                max_vm_count = VALUES(max_vm_count),
                max_cpu_cores = VALUES(max_cpu_cores),
                max_memory_mb = VALUES(max_memory_mb),
                max_disk_gb = VALUES(max_disk_gb),
                max_power_actions_per_hour = VALUES(max_power_actions_per_hour),
                updated_at = VALUES(updated_at)
            """,
            ("@user_id", limits.UserId),
            ("@max_vm_count", Math.Max(0, limits.MaxVmCount)),
            ("@max_cpu_cores", Math.Max(0, limits.MaxCpuCores)),
            ("@max_memory_mb", Math.Max(0, limits.MaxMemoryMb)),
            ("@max_disk_gb", Math.Max(0, limits.MaxDiskGb)),
            ("@max_power_actions_per_hour", Math.Max(0, limits.MaxPowerActionsPerHour)),
            ("@updated_at", DateTime.UtcNow));
    }

    public async Task<int> CountRecentPowerActionsAsync(long userId, DateTime sinceUtc)
    {
        return (int)Math.Min(int.MaxValue, await ScalarAsync(
            "SELECT COUNT(*) FROM user_power_actions WHERE user_id = @user_id AND created_at >= @since",
            ("@user_id", userId),
            ("@since", sinceUtc)));
    }

    public Task AddPowerActionAsync(long userId, string vmName, string action) =>
        ExecuteAsync("""
            INSERT INTO user_power_actions (user_id, vm_name, action)
            VALUES (@user_id, @vm_name, @action)
            """,
            ("@user_id", userId),
            ("@vm_name", vmName),
            ("@action", action));

    public async Task<IReadOnlyList<VmNetworkPortProfile>> GetAllVmNetworkPortProfilesAsync()
    {
        var profiles = new List<VmNetworkPortProfile>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, nat_name, internal_ip_address, public_ip_address, public_hostname,
                   enable_rdp, rdp_external_port, rdp_internal_port,
                   enable_fivem, fivem_external_port, fivem_internal_port,
                   extra_tcp_ports, extra_udp_ports, dns_interface_alias,
                   preferred_dns_server, alternate_dns_server, updated_at
            FROM vm_network_profiles
            ORDER BY vm_name
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            profiles.Add(new VmNetworkPortProfile(
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                !reader.IsDBNull(5) && reader.GetInt64(5) == 1,
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? 3389 : reader.GetInt32(7),
                !reader.IsDBNull(8) && reader.GetInt64(8) == 1,
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? 30120 : reader.GetInt32(10),
                reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
                reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
                ReadDate(reader, 16)));
        }

        return profiles;
    }

    public async Task<VmPublicStatusSettings> GetVmPublicStatusSettingsAsync(string vmName)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, is_public, display_name, public_note, port_check_port, updated_at
            FROM vm_public_status
            WHERE vm_name = @vm_name
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return new VmPublicStatusSettings(vmName, false, vmName, string.Empty, null, DateTime.UtcNow);
        }

        return new VmPublicStatusSettings(
            reader.GetString(0),
            !reader.IsDBNull(1) && reader.GetInt64(1) == 1,
            reader.IsDBNull(2) ? vmName : reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            ReadDate(reader, 5));
    }

    public async Task<IReadOnlyList<VmPublicStatusSettings>> GetPublicVmStatusSettingsAsync(bool publicOnly = true)
    {
        var items = new List<VmPublicStatusSettings>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = publicOnly
            ? "SELECT vm_name, is_public, display_name, public_note, port_check_port, updated_at FROM vm_public_status WHERE is_public = 1 ORDER BY display_name, vm_name"
            : "SELECT vm_name, is_public, display_name, public_note, port_check_port, updated_at FROM vm_public_status ORDER BY display_name, vm_name";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new VmPublicStatusSettings(
                reader.GetString(0),
                !reader.IsDBNull(1) && reader.GetInt64(1) == 1,
                reader.IsDBNull(2) ? reader.GetString(0) : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                ReadDate(reader, 5)));
        }

        return items;
    }

    public async Task SaveVmPublicStatusSettingsAsync(VmPublicStatusSettings settings)
    {
        await ExecuteAsync("""
            INSERT INTO vm_public_status (vm_name, is_public, display_name, public_note, port_check_port, updated_at)
            VALUES (@vm_name, @is_public, @display_name, @public_note, @port_check_port, @updated_at)
            ON DUPLICATE KEY UPDATE
                is_public = VALUES(is_public),
                display_name = VALUES(display_name),
                public_note = VALUES(public_note),
                port_check_port = VALUES(port_check_port),
                updated_at = VALUES(updated_at)
            """,
            ("@vm_name", settings.VmName),
            ("@is_public", settings.IsPublic ? 1 : 0),
            ("@display_name", string.IsNullOrWhiteSpace(settings.DisplayName) ? settings.VmName : settings.DisplayName.Trim()),
            ("@public_note", settings.PublicNote ?? string.Empty),
            ("@port_check_port", settings.PortCheckPort),
            ("@updated_at", DateTime.UtcNow));
    }


    public async Task<VmGuestAgentSettings> GetVmGuestAgentSettingsAsync(string vmName)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, is_enabled, token_hash, last_seen_at, last_agent_version, last_ip_address, updated_at
            FROM vm_guest_agents
            WHERE vm_name = @vm_name
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return new VmGuestAgentSettings(vmName, false, false, null, string.Empty, string.Empty, DateTime.UtcNow);
        }

        return new VmGuestAgentSettings(
            reader.GetString(0),
            !reader.IsDBNull(1) && Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) == 1,
            !reader.IsDBNull(2) && !string.IsNullOrWhiteSpace(reader.GetString(2)),
            reader.IsDBNull(3) ? null : ReadDate(reader, 3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            ReadDate(reader, 6));
    }

    public async Task<VmGuestAgentSettings> SaveVmGuestAgentTokenAsync(string vmName, string plainToken)
    {
        await ExecuteAsync("""
            INSERT INTO vm_guest_agents (vm_name, is_enabled, token_hash, updated_at)
            VALUES (@vm_name, 1, @token_hash, @updated_at)
            ON DUPLICATE KEY UPDATE
                is_enabled = 1,
                token_hash = VALUES(token_hash),
                updated_at = VALUES(updated_at)
            """,
            ("@vm_name", vmName),
            ("@token_hash", HashSessionToken(plainToken)),
            ("@updated_at", DateTime.UtcNow));

        return await GetVmGuestAgentSettingsAsync(vmName);
    }

    public async Task<VmGuestAgentSettings> SetVmGuestAgentEnabledAsync(string vmName, bool enabled)
    {
        await ExecuteAsync("""
            INSERT INTO vm_guest_agents (vm_name, is_enabled, updated_at)
            VALUES (@vm_name, @is_enabled, @updated_at)
            ON DUPLICATE KEY UPDATE
                is_enabled = VALUES(is_enabled),
                updated_at = VALUES(updated_at)
            """,
            ("@vm_name", vmName),
            ("@is_enabled", enabled ? 1 : 0),
            ("@updated_at", DateTime.UtcNow));

        return await GetVmGuestAgentSettingsAsync(vmName);
    }

    public async Task<VmGuestAgentSettings?> GetVmGuestAgentByTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var tokenHash = HashSessionToken(token.Trim());
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, is_enabled, token_hash, last_seen_at, last_agent_version, last_ip_address, updated_at
            FROM vm_guest_agents
            WHERE token_hash = @token_hash AND is_enabled = 1
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@token_hash", tokenHash);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new VmGuestAgentSettings(
            reader.GetString(0),
            !reader.IsDBNull(1) && Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) == 1,
            !reader.IsDBNull(2) && !string.IsNullOrWhiteSpace(reader.GetString(2)),
            reader.IsDBNull(3) ? null : ReadDate(reader, 3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            ReadDate(reader, 6));
    }

    public async Task<VmGuestAgentSettings> RegisterGuestAgentHeartbeatAsync(string token, string agentVersion, string ipAddress)
    {
        var agent = await GetVmGuestAgentByTokenAsync(token)
            ?? throw new InvalidOperationException("Guest agent token is invalid or disabled.");

        await ExecuteAsync("""
            UPDATE vm_guest_agents
            SET last_seen_at = @last_seen_at,
                last_agent_version = @last_agent_version,
                last_ip_address = @last_ip_address,
                updated_at = @updated_at
            WHERE vm_name = @vm_name
            """,
            ("@vm_name", agent.VmName),
            ("@last_seen_at", DateTime.UtcNow),
            ("@last_agent_version", (agentVersion ?? string.Empty).Trim()),
            ("@last_ip_address", (ipAddress ?? string.Empty).Trim()),
            ("@updated_at", DateTime.UtcNow));

        return await GetVmGuestAgentSettingsAsync(agent.VmName);
    }

    public async Task<long> CreateGuestAgentCommandAsync(string vmName, string commandType, string payloadJson)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO guest_agent_commands (vm_name, command_type, status, payload_secret, created_at)
            VALUES (@vm_name, @command_type, 'pending', @payload_secret, @created_at)
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        cmd.Parameters.AddWithValue("@command_type", commandType);
        cmd.Parameters.AddWithValue("@payload_secret", _secrets.Protect(payloadJson ?? string.Empty));
        cmd.Parameters.AddWithValue("@created_at", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync();
        return cmd.LastInsertedId;
    }

    public async Task<IReadOnlyList<GuestAgentCommand>> GetGuestAgentCommandsForVmAsync(string vmName, int limit = 15)
    {
        var items = new List<GuestAgentCommand>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, vm_name, command_type, status, payload_secret, result_message, created_at, started_at, completed_at
            FROM guest_agent_commands
            WHERE vm_name = @vm_name
            ORDER BY id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@vm_name", vmName);
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 100));
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new GuestAgentCommand(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                string.Empty,
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                ReadDate(reader, 6),
                reader.IsDBNull(7) ? null : ReadDate(reader, 7),
                reader.IsDBNull(8) ? null : ReadDate(reader, 8)));
        }

        return items;
    }

    public async Task<GuestAgentCommand?> StartNextGuestAgentCommandAsync(string token)
    {
        var agent = await GetVmGuestAgentByTokenAsync(token)
            ?? throw new InvalidOperationException("Guest agent token is invalid or disabled.");

        await using var db = Open();
        await using (var retry = db.CreateCommand())
        {
            retry.CommandText = """
                UPDATE guest_agent_commands
                SET status = 'pending', started_at = NULL
                WHERE vm_name = @vm_name AND status = 'running' AND started_at < @cutoff
                """;
            retry.Parameters.AddWithValue("@vm_name", agent.VmName);
            retry.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddMinutes(-15));
            await retry.ExecuteNonQueryAsync();
        }

        long commandId = 0;
        await using (var select = db.CreateCommand())
        {
            select.CommandText = """
                SELECT id
                FROM guest_agent_commands
                WHERE vm_name = @vm_name AND status = 'pending'
                ORDER BY id ASC
                LIMIT 1
                """;
            select.Parameters.AddWithValue("@vm_name", agent.VmName);
            var value = await select.ExecuteScalarAsync();
            if (value is null || value == DBNull.Value)
            {
                return null;
            }

            commandId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        await using (var update = db.CreateCommand())
        {
            update.CommandText = """
                UPDATE guest_agent_commands
                SET status = 'running', started_at = @started_at
                WHERE id = @id AND vm_name = @vm_name AND status = 'pending'
                """;
            update.Parameters.AddWithValue("@id", commandId);
            update.Parameters.AddWithValue("@vm_name", agent.VmName);
            update.Parameters.AddWithValue("@started_at", DateTime.UtcNow);
            var changed = await update.ExecuteNonQueryAsync();
            if (changed == 0)
            {
                return null;
            }
        }

        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, vm_name, command_type, status, payload_secret, result_message, created_at, started_at, completed_at
            FROM guest_agent_commands
            WHERE id = @id AND vm_name = @vm_name
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@id", commandId);
        cmd.Parameters.AddWithValue("@vm_name", agent.VmName);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new GuestAgentCommand(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            _secrets.Unprotect(reader.IsDBNull(4) ? string.Empty : reader.GetString(4)),
            reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            ReadDate(reader, 6),
            reader.IsDBNull(7) ? null : ReadDate(reader, 7),
            reader.IsDBNull(8) ? null : ReadDate(reader, 8));
    }

    public async Task CompleteGuestAgentCommandAsync(string token, long commandId, bool success, string? message)
    {
        var agent = await GetVmGuestAgentByTokenAsync(token)
            ?? throw new InvalidOperationException("Guest agent token is invalid or disabled.");

        var resultMessage = message ?? string.Empty;
        if (resultMessage.Length > 4000)
        {
            resultMessage = resultMessage[..4000];
        }

        await ExecuteAsync("""
            UPDATE guest_agent_commands
            SET status = @status,
                result_message = @result_message,
                completed_at = @completed_at
            WHERE id = @id AND vm_name = @vm_name AND status = 'running'
            """,
            ("@id", commandId),
            ("@vm_name", agent.VmName),
            ("@status", success ? "completed" : "failed"),
            ("@result_message", resultMessage),
            ("@completed_at", DateTime.UtcNow));
    }

    public async Task<IReadOnlyList<VmRecoveryInventory>> GetAllVmRecoveryInventoryAsync()
    {
        var result = new List<VmRecoveryInventory>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT vm_name, is_enabled, username,
                   CASE WHEN password_secret IS NOT NULL AND password_secret <> '' THEN 1 ELSE 0 END,
                   COALESCE(last_error, ''), updated_at
            FROM vm_recovery_accounts
            ORDER BY vm_name
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new VmRecoveryInventory(
                reader.GetString(0),
                !reader.IsDBNull(1) && reader.GetBoolean(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                !reader.IsDBNull(3) && reader.GetInt32(3) == 1,
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                ReadDate(reader, 5)));
        }

        return result;
    }

    public async Task<IReadOnlyList<WhmcsServiceRecord>> GetAllWhmcsServicesAsync()
    {
        var result = new List<WhmcsServiceRecord>();
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT service_id, client_id, panel_user_id, vm_name, status,
                   COALESCE(last_error, ''), created_at, updated_at
            FROM whmcs_services
            ORDER BY service_id
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new WhmcsServiceRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                ReadDate(reader, 6),
                ReadDate(reader, 7)));
        }

        return result;
    }

    public async Task<WhmcsServiceRecord?> GetWhmcsServiceAsync(long serviceId)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT service_id, client_id, panel_user_id, vm_name, status, COALESCE(last_error, ''), created_at, updated_at
            FROM whmcs_services
            WHERE service_id = @service_id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@service_id", serviceId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new WhmcsServiceRecord(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            ReadDate(reader, 6),
            ReadDate(reader, 7));
    }

    public async Task<long?> GetWhmcsPanelUserIdForClientAsync(long clientId)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT panel_user_id
            FROM whmcs_services
            WHERE client_id = @client_id
            ORDER BY updated_at DESC, service_id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@client_id", clientId);
        var value = await cmd.ExecuteScalarAsync();
        return value is null || value is DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task<bool> IsWhmcsVmNameInUseAsync(string vmName, long excludeServiceId = 0)
    {
        vmName = (vmName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(vmName))
        {
            return false;
        }

        return await ScalarAsync("""
            SELECT COUNT(*)
            FROM whmcs_services
            WHERE lower(vm_name) = lower(@vm_name)
              AND (@exclude_service_id <= 0 OR service_id <> @exclude_service_id)
            """,
            ("@vm_name", vmName),
            ("@exclude_service_id", excludeServiceId)) > 0;
    }

    public async Task CreateWhmcsServiceAsync(WhmcsServiceRecord service)
    {
        await ExecuteAsync("""
            INSERT INTO whmcs_services (service_id, client_id, panel_user_id, vm_name, status, last_error, created_at, updated_at)
            VALUES (@service_id, @client_id, @panel_user_id, @vm_name, @status, @last_error, @created_at, @updated_at)
            """,
            ("@service_id", service.ServiceId),
            ("@client_id", service.ClientId),
            ("@panel_user_id", service.PanelUserId),
            ("@vm_name", service.VmName),
            ("@status", service.Status),
            ("@last_error", service.LastError ?? string.Empty),
            ("@created_at", service.CreatedAt),
            ("@updated_at", service.UpdatedAt));
    }

    public async Task UpdateWhmcsServiceStatusAsync(long serviceId, string status, string lastError)
    {
        await ExecuteAsync("""
            UPDATE whmcs_services
            SET status = @status,
                last_error = @last_error,
                updated_at = @updated_at
            WHERE service_id = @service_id
            """,
            ("@service_id", serviceId),
            ("@status", status ?? string.Empty),
            ("@last_error", lastError ?? string.Empty),
            ("@updated_at", DateTime.UtcNow));
    }

    public async Task ClearResolvedWhmcsRecoveryWarningByVmNameAsync(string vmName)
    {
        vmName = (vmName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(vmName))
        {
            return;
        }

        const string recoveryWarningPrefix =
            "VPS was created, but agentless emergency password recovery could not be initialized:";

        await using var db = Open();
        await using var select = db.CreateCommand();
        select.CommandText = """
            SELECT service_id, COALESCE(last_error, '')
            FROM whmcs_services
            WHERE lower(vm_name) = lower(@vm_name)
              AND status <> 'terminated'
            ORDER BY updated_at DESC, service_id DESC
            LIMIT 1
            """;
        select.Parameters.AddWithValue("@vm_name", vmName);

        long? serviceId = null;
        var lastError = string.Empty;
        await using (var reader = await select.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                serviceId = reader.GetInt64(0);
                lastError = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
        }

        if (serviceId is null || string.IsNullOrWhiteSpace(lastError))
        {
            return;
        }

        var remaining = lastError
            .Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !item.StartsWith(recoveryWarningPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var cleaned = string.Join(" | ", remaining);
        if (string.Equals(cleaned, lastError, StringComparison.Ordinal))
        {
            return;
        }

        await using var update = db.CreateCommand();
        update.CommandText = """
            UPDATE whmcs_services
            SET last_error = @last_error,
                updated_at = @updated_at
            WHERE service_id = @service_id
            """;
        update.Parameters.AddWithValue("@service_id", serviceId.Value);
        update.Parameters.AddWithValue("@last_error", cleaned);
        update.Parameters.AddWithValue("@updated_at", DateTime.UtcNow);
        await update.ExecuteNonQueryAsync();
    }

    public async Task DeleteFailedWhmcsServiceAsync(long serviceId)
    {
        // This intentionally removes only the WHMCS idempotency/mapping row. It is used by
        // WhmcsApiService only after it has verified that the service is in an error state and
        // that no Hyper-V VM with the recorded name exists. Normal/active services are never
        // cleared by this method.
        await ExecuteAsync("""
            DELETE FROM whmcs_services
            WHERE service_id = @service_id
              AND status = 'error'
            """,
            ("@service_id", serviceId));
    }

    public async Task DeleteTerminatedWhmcsServiceForRecreateAsync(long serviceId)
    {
        // This does NOT delete a VM or customer. It removes only the old idempotency/mapping
        // row after WhmcsApiService has verified the record is terminated and the former
        // Hyper-V VM no longer exists. Audit history lives in audit_logs and is preserved.
        await ExecuteAsync("""
            DELETE FROM whmcs_services
            WHERE service_id = @service_id
              AND status = 'terminated'
            """,
            ("@service_id", serviceId));
    }

    public async Task MarkWhmcsManagedUserAsync(long panelUserId, long clientId)
    {
        await ExecuteAsync("""
            INSERT INTO whmcs_managed_users (panel_user_id, client_id, created_at)
            VALUES (@panel_user_id, @client_id, @created_at)
            ON DUPLICATE KEY UPDATE client_id = VALUES(client_id)
            """,
            ("@panel_user_id", panelUserId),
            ("@client_id", clientId),
            ("@created_at", DateTime.UtcNow));
    }

    public async Task<bool> IsWhmcsManagedUserAsync(long panelUserId)
    {
        return await ScalarAsync("""
            SELECT COUNT(*)
            FROM whmcs_managed_users
            WHERE panel_user_id = @panel_user_id
            """,
            ("@panel_user_id", panelUserId)) > 0;
    }

    public async Task<bool> WasUserCreatedByWhmcsApiAsync(long panelUserId, string username)
    {
        username = (username ?? string.Empty).Trim();
        var idNeedle = $"%id: {panelUserId},%";

        return await ScalarAsync("""
            SELECT COUNT(*)
            FROM audit_logs
            WHERE action = 'USER_CREATED'
              AND lower(COALESCE(details, '')) LIKE '%by whmcs api%'
              AND (
                    resource_id = @username
                    OR COALESCE(details, '') LIKE @id_needle
                  )
            """,
            ("@username", username),
            ("@id_needle", idNeedle)) > 0;
    }

    public async Task<long> CountNonTerminatedWhmcsServicesForUserAsync(long panelUserId)
    {
        return await ScalarAsync("""
            SELECT COUNT(*)
            FROM whmcs_services
            WHERE panel_user_id = @panel_user_id
              AND lower(status) <> 'terminated'
            """,
            ("@panel_user_id", panelUserId));
    }

    public async Task<long> CountUserVmAssignmentsAsync(long panelUserId)
    {
        return await ScalarAsync("""
            SELECT COUNT(*)
            FROM user_vm_assignments
            WHERE user_id = @panel_user_id
            """,
            ("@panel_user_id", panelUserId));
    }

    public async Task DeleteWhmcsManagedUserMarkerAsync(long panelUserId)
    {
        await ExecuteAsync("""
            DELETE FROM whmcs_managed_users
            WHERE panel_user_id = @panel_user_id
            """,
            ("@panel_user_id", panelUserId));
    }

    private async Task<Dictionary<string, string>> GetSettingsAsync()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT `key`, value FROM settings";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            settings[reader.GetString(0)] = reader.GetString(1);
        }

        return settings;
    }

    private async Task ApplyAvailabilityOneHourDefaultMigrationAsync()
    {
        const string migrationKey = "availability_extended_downtime_default_60_applied";
        var alreadyApplied = await ScalarAsync(
            "SELECT COUNT(*) FROM settings WHERE `key` = @key",
            ("@key", migrationKey)) > 0;
        if (alreadyApplied)
        {
            return;
        }

        // Change only the previous built-in three-hour default. Any custom value
        // chosen by an administrator is preserved.
        await ExecuteAsync("""
            UPDATE settings
            SET value = '60'
            WHERE `key` = 'availability_extended_downtime_minutes'
              AND trim(value) = '180'
            """);
        await SetSettingAsync(migrationKey, "1");
    }

    private async Task EnsureSettingAsync(string key, string value)
    {
        var exists = await ScalarAsync("SELECT COUNT(*) FROM settings WHERE `key` = @key", ("@key", key)) > 0;
        if (!exists)
        {
            await SetSettingAsync(key, value);
        }
    }

    private async Task SetSettingAsync(string key, string value)
    {
        await ExecuteAsync("""
            INSERT INTO settings (`key`, value)
            VALUES (@key, @value)
            ON DUPLICATE KEY UPDATE value = VALUES(value)
            """,
            ("@key", key),
            ("@value", value));
    }

    private async Task<long> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var db = Open();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var parameter in parameters)
        {
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var db = Open();
        await ExecuteAsync(db, sql, parameters);
    }

    private static async Task<long> ScalarAsync(MySqlConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var parameter in parameters)
        {
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(MySqlConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var parameter in parameters)
        {
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(MySqlConnection db, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table_name
            """;
        cmd.Parameters.AddWithValue("@table_name", tableName);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static string HashSessionToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private static double ParseDoubleSetting(string? value, double fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariantValue))
        {
            return invariantValue;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var currentCultureValue))
        {
            return currentCultureValue;
        }

        var normalized = value.Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var normalizedValue))
        {
            return normalizedValue;
        }

        return fallback;
    }

    private static double NormalizeAbuseThreshold(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return fallback;
        }

        // Earlier builds used 0.03/0.05 Mbps defaults, which catches normal VM chatter.
        return value < 1 ? fallback : value;
    }

    private static int ParseIntSetting(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static UserVmPermissions ReadPermissions(MySqlDataReader reader, int offset)
    {
        return new UserVmPermissions(
            ReadBool(reader, offset),
            ReadBool(reader, offset + 1),
            ReadBool(reader, offset + 2),
            ReadBool(reader, offset + 3),
            ReadBool(reader, offset + 4),
            ReadBool(reader, offset + 5),
            ReadBool(reader, offset + 6),
            ReadBool(reader, offset + 7),
            ReadBool(reader, offset + 8));
    }

    private static bool ReadBool(MySqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return false;
        return Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture) != 0;
    }

    private static int ToInt(bool value) => value ? 1 : 0;

    private static DateTime ReadDate(MySqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return DateTime.UtcNow;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dateTime => dateTime,
            string text when DateTime.TryParse(text, out var parsed) => parsed,
            _ => DateTime.UtcNow
        };
    }

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : DateTime.UtcNow;
}
