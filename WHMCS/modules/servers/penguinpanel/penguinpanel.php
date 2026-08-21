<?php

if (!defined('WHMCS')) {
    die('This file cannot be accessed directly.');
}

require_once __DIR__ . '/lib/ApiClient.php';

/**
 * PenguinPanel WHMCS Provisioning Module
 *
 * Connects WHMCS to the narrow /api/whmcs/* API exposed by PenguinPanel.
 * WHMCS never receives Hyper-V or PowerShell access.
 */

function penguinpanel_MetaData()
{
    return [
        'DisplayName' => 'PenguinPanel Hyper-V',
        'APIVersion' => '1.1',
        'RequiresServer' => true,
        'DefaultNonSSLPort' => '80',
        'DefaultSSLPort' => '443',
    ];
}

function penguinpanel_ConfigOptions($params = [])
{
    return [
        'Template ID' => [
            'FriendlyName' => 'OS Template',
            'Type' => 'text',
            'Size' => '18',
            'Default' => '',
            'Description' => 'Select an enabled PenguinPanel VHD/VHDX template. Loaded live from the API when a server is assigned.',
            'Loader' => 'penguinpanel_LoadTemplates',
            'SimpleMode' => true,
        ],
        'CPU Cores' => [
            'FriendlyName' => 'CPU Cores',
            'Type' => 'text',
            'Size' => '8',
            'Default' => '2',
            'Description' => 'Default vCPU count. A WHMCS configurable option named CPU Cores/vCPU can override it.',
            'SimpleMode' => true,
        ],
        'Memory MB' => [
            'FriendlyName' => 'Memory (MB)',
            'Type' => 'text',
            'Size' => '10',
            'Default' => '4096',
            'Description' => 'Default RAM in MB. Configurable options named Memory MB/RAM/Memory can override it.',
            'SimpleMode' => true,
        ],
        'Disk GB' => [
            'FriendlyName' => 'Disk (GB)',
            'Type' => 'text',
            'Size' => '10',
            'Default' => '128',
            'Description' => 'Requested VHD/VHDX size. PenguinPanel can grow templates, not shrink them.',
            'SimpleMode' => true,
        ],
        'Generation' => [
            'FriendlyName' => 'Hyper-V Generation',
            'Type' => 'dropdown',
            'Options' => '1,2',
            'Default' => '2',
            'SimpleMode' => true,
        ],
        'Secure Boot' => [
            'FriendlyName' => 'Secure Boot',
            'Type' => 'yesno',
            'Description' => 'Enable Secure Boot for Generation 2 VMs.',
            'Default' => 'on',
            'SimpleMode' => true,
        ],
        'Start After Provision' => [
            'FriendlyName' => 'Start After Provision',
            'Type' => 'yesno',
            'Description' => 'Start the VM after provisioning.',
            'Default' => 'on',
            'SimpleMode' => true,
        ],
        'Configure Windows Guest' => [
            'FriendlyName' => 'Configure Windows Guest',
            'Type' => 'yesno',
            'Description' => 'Use Hyper-V PowerShell Direct for IP/DNS/password/RDP/firewall configuration.',
            'Default' => 'on',
            'SimpleMode' => true,
        ],
        'Guest Username' => [
            'FriendlyName' => 'Windows Username',
            'Type' => 'text',
            'Size' => '20',
            'Default' => 'Administrator',
            'SimpleMode' => true,
        ],
        'Template Password' => [
            'FriendlyName' => 'Template Administrator Password',
            'Type' => 'password',
            'Size' => '32',
            'Description' => 'Current password inside the golden Windows template. Required when automatic guest configuration is enabled.',
        ],
        'Storage Root' => [
            'FriendlyName' => 'VM Storage Root',
            'Type' => 'text',
            'Size' => '32',
            'Default' => '',
            'Description' => 'Select an approved PenguinPanel storage root. The API-backed dropdown is shown in Simple Mode; blank/Automatic uses the PenguinPanel default.',
            'Loader' => 'penguinpanel_LoadStorageRoots',
            'SimpleMode' => true,
        ],
        'Enable FiveM' => [
            'FriendlyName' => 'Enable FiveM Mapping',
            'Type' => 'yesno',
            'Description' => 'Create FiveM TCP + UDP mappings. Leave the public FiveM port at 0 for automatic allocation.',
            'Default' => '',
            'SimpleMode' => true,
        ],
        'FiveM Internal Port' => [
            'FriendlyName' => 'FiveM Internal Port',
            'Type' => 'text',
            'Size' => '10',
            'Default' => '30120',
            'Description' => 'Port FiveM listens on inside the VPS. Normally 30120. This is not the public internet port.',
        ],
        'Public RDP Port' => [
            'FriendlyName' => 'Public RDP Port',
            'Type' => 'text',
            'Size' => '10',
            'Default' => '0',
            'Description' => 'Recommended: leave 0 so PenguinPanel allocates a collision-safe RDP port automatically.',
        ],
        'Public FiveM Port' => [
            'FriendlyName' => 'Public FiveM Port',
            'Type' => 'text',
            'Size' => '10',
            'Default' => '0',
            'Description' => 'Recommended: leave 0 so PenguinPanel allocates a collision-safe FiveM port automatically.',
        ],
        'RDP Customer Hostname' => [
            'FriendlyName' => 'RDP Customer Hostname',
            'Type' => 'text',
            'Size' => '36',
            'Default' => 'remotedesktop.penguinhosting.host',
            'Description' => 'Customer-facing hostname for Remote Desktop. This only changes WHMCS/client/email display; PenguinPanel networking still uses the real public IP. Legacy PH-VPS- values are automatically ignored.',
        ],
    ];
}

function penguinpanel_LoadTemplates($params)
{
    $client = penguinpanel_client($params);
    $catalog = $client->get('/api/whmcs/catalog', 30);
    $result = [];

    foreach (($catalog['templates'] ?? []) as $template) {
        $id = (string) ($template['id'] ?? '');
        if ($id === '') {
            continue;
        }
        $name = trim((string) ($template['name'] ?? ('Template ' . $id)));
        $os = trim((string) ($template['operatingSystem'] ?? ''));
        $label = $name . ($os !== '' ? ' — ' . $os : '') . ' (ID ' . $id . ')';
        $result[$id] = $label;
    }

    if (!$result) {
        throw new Exception('PenguinPanel returned no enabled OS templates.');
    }

    return $result;
}


function penguinpanel_resolveTemplateId(array $params, $selection)
{
    $client = penguinpanel_client($params);
    $catalog = $client->get('/api/whmcs/catalog', 30);
    $templates = [];

    foreach (($catalog['templates'] ?? []) as $template) {
        $id = (int) ($template['id'] ?? 0);
        if ($id <= 0) {
            continue;
        }

        $name = trim((string) ($template['name'] ?? ('Template ' . $id)));
        $templates[$id] = $name;
    }

    if (!$templates) {
        throw new Exception('PenguinPanel returned no enabled VHD/VHDX templates. Add or enable a template in PenguinPanel first.');
    }

    $raw = trim((string) $selection);
    if ($raw !== '' && preg_match('/^\d+$/', $raw)) {
        $id = (int) $raw;
        if (isset($templates[$id])) {
            return $id;
        }
    }

    if ($raw !== '') {
        foreach ($templates as $id => $name) {
            if (strcasecmp($raw, $name) === 0) {
                return (int) $id;
            }
        }
    }

    // Safe compatibility path for products created with the old v1.1.1 default
    // Template ID 1: when PenguinPanel exposes exactly one enabled template,
    // there is no ambiguity, so use that template automatically.
    if (count($templates) === 1) {
        return (int) array_key_first($templates);
    }

    $available = [];
    foreach ($templates as $id => $name) {
        $available[] = $name . ' (ID ' . $id . ')';
    }

    $selectedText = $raw === '' ? 'none' : $raw;
    throw new Exception(
        'The selected PenguinPanel OS template (' . $selectedText . ') is not valid. '
        . 'Available templates: ' . implode(', ', $available)
        . '. Open Product > Module Settings and select the correct OS Template.'
    );
}

function penguinpanel_LoadStorageRoots($params)
{
    $catalog = penguinpanel_client($params)->get('/api/whmcs/catalog', 30);
    return penguinpanel_storageRootOptions($catalog, null, false);
}

function penguinpanel_storageRootOptions(array $catalog, $diskGb = null, $disableUnsafe = false)
{
    $defaultRoot = trim((string) ($catalog['defaultStorageRoot'] ?? ''));
    $reserveGb = max(0, (int) ($catalog['minimumFreeSpaceAfterProvisionGb'] ?? 0));
    $reservePercent = max(0, (int) ($catalog['minimumFreeSpaceAfterProvisionPercent'] ?? 0));
    $mode = trim((string) ($catalog['storageSelectionMode'] ?? 'MostFreeSpace'));
    $details = $catalog['storageRootDetails'] ?? [];

    $recommended = penguinpanel_recommendedStorageRoot($catalog, $diskGb);
    $automaticLabel = 'Automatic — PenguinPanel chooses the safest drive';
    if ($recommended !== '') {
        $automaticLabel .= ' (' . $recommended . ' recommended)';
    }
    if ($reserveGb > 0 || $reservePercent > 0) {
        $reserveParts = [];
        if ($reserveGb > 0) {
            $reserveParts[] = $reserveGb . ' GB';
        }
        if ($reservePercent > 0) {
            $reserveParts[] = $reservePercent . '%';
        }
        $automaticLabel .= ' — reserve ' . implode(' / ', $reserveParts);
    }

    $result = ['' => $automaticLabel];
    if (is_array($details) && count($details) > 0) {
        foreach ($details as $item) {
            if (!is_array($item)) {
                continue;
            }
            $root = trim((string) ($item['path'] ?? ''));
            if ($root === '') {
                continue;
            }
            $available = !empty($item['isAvailable']);
            $freeBytes = (float) ($item['freeBytes'] ?? 0);
            $totalBytes = (float) ($item['totalBytes'] ?? 0);
            $label = $root;
            if ($available) {
                $label .= ' — ' . penguinpanel_formatBytes($freeBytes) . ' free';
                if ($totalBytes > 0) {
                    $label .= ' / ' . penguinpanel_formatBytes($totalBytes) . ' total';
                }
                if (!empty($item['isDefault'])) {
                    $label .= ' — Default';
                }
                if ($diskGb !== null && $diskGb > 0 && !penguinpanel_storageCanFit($item, (int) $diskGb, $reserveGb, $reservePercent)) {
                    $label .= ' — NOT ENOUGH SPACE';
                }
            } else {
                $label .= ' — Unavailable';
            }
            $result[$root] = $label;
        }
        return $result;
    }

    foreach (($catalog['storageRoots'] ?? []) as $root) {
        $root = trim((string) $root);
        if ($root !== '') {
            $result[$root] = $root . (strcasecmp($root, $defaultRoot) === 0 ? ' — Default' : '');
        }
    }

    return $result;
}

function penguinpanel_resolveStorageRoot(array $params, $selection)
{
    $catalog = penguinpanel_client($params)->get('/api/whmcs/catalog', 30);
    $roots = [];
    foreach (($catalog['storageRoots'] ?? []) as $root) {
        $root = trim((string) $root);
        if ($root !== '') {
            $roots[] = $root;
        }
    }

    if (!$roots) {
        throw new Exception('PenguinPanel returned no approved VM storage roots. Configure Provisioning:VmStorageRoots first.');
    }

    $raw = trim((string) $selection);
    // Blank/Automatic must remain blank all the way to PenguinPanel. The panel
    // will choose the safest root at provisioning time using live free-space data.
    if ($raw === '' || strcasecmp($raw, 'automatic') === 0 || strcasecmp($raw, 'default') === 0 || $raw === '__AUTO__') {
        return '';
    }

    foreach ($roots as $root) {
        if (strcasecmp($raw, $root) === 0) {
            return $root;
        }
    }

    throw new Exception(
        'The selected VM Storage Root (' . $raw . ') is not allowed. Available roots: '
        . implode(', ', $roots)
        . '. Choose Automatic or an approved root.'
    );
}

function penguinpanel_recommendedStorageRoot(array $catalog, $diskGb = null)
{
    $details = $catalog['storageRootDetails'] ?? [];
    if (!is_array($details) || !$details) {
        return trim((string) ($catalog['defaultStorageRoot'] ?? ''));
    }

    $reserveGb = max(0, (int) ($catalog['minimumFreeSpaceAfterProvisionGb'] ?? 0));
    $reservePercent = max(0, (int) ($catalog['minimumFreeSpaceAfterProvisionPercent'] ?? 0));
    $mode = strtolower(trim((string) ($catalog['storageSelectionMode'] ?? 'MostFreeSpace')));
    $eligible = [];
    foreach ($details as $item) {
        if (!is_array($item) || empty($item['isAvailable']) || trim((string) ($item['path'] ?? '')) === '') {
            continue;
        }
        if ($diskGb !== null && $diskGb > 0 && !penguinpanel_storageCanFit($item, (int) $diskGb, $reserveGb, $reservePercent)) {
            continue;
        }
        $eligible[] = $item;
    }
    if (!$eligible) {
        return '';
    }

    if ($mode === 'defaultfirst') {
        foreach ($eligible as $item) {
            if (!empty($item['isDefault'])) {
                return trim((string) $item['path']);
            }
        }
    }

    usort($eligible, function ($a, $b) use ($mode) {
        $aFree = (float) ($a['freeBytes'] ?? 0);
        $bFree = (float) ($b['freeBytes'] ?? 0);
        if ($mode === 'mostfreepercent') {
            $aTotal = max(1.0, (float) ($a['totalBytes'] ?? 0));
            $bTotal = max(1.0, (float) ($b['totalBytes'] ?? 0));
            $cmp = ($bFree / $bTotal) <=> ($aFree / $aTotal);
            if ($cmp !== 0) {
                return $cmp;
            }
        }
        return $bFree <=> $aFree;
    });

    return trim((string) ($eligible[0]['path'] ?? ''));
}

function penguinpanel_storageCanFit(array $item, $diskGb, $reserveGb, $reservePercent)
{
    if (empty($item['isAvailable'])) {
        return false;
    }
    $free = (float) ($item['freeBytes'] ?? 0);
    $total = (float) ($item['totalBytes'] ?? 0);
    $diskBytes = max(0, (int) $diskGb) * 1024 * 1024 * 1024;
    $fixedReserve = max(0, (int) $reserveGb) * 1024 * 1024 * 1024;
    $percentReserve = ($total > 0 && $reservePercent > 0) ? ceil($total * ($reservePercent / 100)) : 0;
    $reserve = max($fixedReserve, $percentReserve);
    return $free >= ($diskBytes + $reserve);
}

function penguinpanel_formatBytes($bytes)
{
    $bytes = max(0, (float) $bytes);
    $tb = 1024 * 1024 * 1024 * 1024;
    $gb = 1024 * 1024 * 1024;
    if ($bytes >= $tb) {
        return number_format($bytes / $tb, 2) . ' TB';
    }
    return number_format($bytes / $gb, 1) . ' GB';
}

function penguinpanel_TestConnection(array $params)
{
    try {
        $client = penguinpanel_client($params);
        $health = $client->get('/api/whmcs/health', 30);
        $catalog = $client->get('/api/whmcs/catalog', 30);

        if (($health['status'] ?? '') !== 'ok') {
            throw new Exception('PenguinPanel health endpoint did not return status=ok.');
        }

        return [
            'success' => true,
            'error' => '',
        ];
    } catch (Throwable $e) {
        penguinpanel_log('TestConnection', ['baseUrl' => penguinpanel_baseUrl($params)], ['error' => $e->getMessage()]);
        return [
            'success' => false,
            'error' => $e->getMessage(),
        ];
    }
}

function penguinpanel_CreateAccount(array $params)
{
    try {
        $serviceId = (int) ($params['serviceid'] ?? 0);
        $clientId = (int) ($params['userid'] ?? ($params['clientsdetails']['userid'] ?? 0));
        if ($serviceId <= 0 || $clientId <= 0) {
            throw new Exception('WHMCS did not supply a valid service/client ID.');
        }

        $templateSelection = penguinpanel_value($params, 1, ['Template ID', 'OS Template', 'Template'], '');
        $templateId = penguinpanel_resolveTemplateId($params, $templateSelection);
        $cpu = penguinpanel_intValue($params, 2, ['CPU Cores', 'CPU', 'vCPU', 'vCPUs'], 2);
        $memory = penguinpanel_intValue($params, 3, ['Memory MB', 'Memory', 'RAM', 'RAM MB'], 4096);
        $disk = penguinpanel_intValue($params, 4, ['Disk GB', 'Disk', 'Storage GB', 'Storage'], 128);
        $generation = penguinpanel_intValue($params, 5, ['Generation', 'Hyper-V Generation'], 2);
        $secureBoot = penguinpanel_boolValue($params, 6, ['Secure Boot'], true);
        $startAfter = penguinpanel_boolValue($params, 7, ['Start After Provision'], true);
        $configureGuest = penguinpanel_boolValue($params, 8, ['Configure Windows Guest'], true);
        $guestUsername = trim((string) penguinpanel_value($params, 9, ['Guest Username', 'Windows Username'], 'Administrator'));
        $templatePassword = (string) penguinpanel_value($params, 10, ['Template Password', 'Template Administrator Password'], '');
        $productStorageSelection = trim((string) penguinpanel_value($params, 11, ['Storage Root', 'VM Storage Root'], ''));
        $serviceStorageOverride = trim((string) penguinpanel_property($params, 'VM Storage Root Override', ''));
        if ($serviceStorageOverride === '__AUTO__') {
            $storageRootSelection = '';
        } elseif ($serviceStorageOverride !== '') {
            $storageRootSelection = $serviceStorageOverride;
        } else {
            $storageRootSelection = $productStorageSelection;
        }
        $storageRoot = penguinpanel_resolveStorageRoot($params, $storageRootSelection);
        $enableFiveM = penguinpanel_boolValue($params, 12, ['Enable FiveM', 'FiveM', 'Enable FiveM Mapping'], false);
        $fiveMInternal = penguinpanel_intValue($params, 13, ['FiveM Internal Port', 'FiveM VM Port'], 30120);
        $rdpExternal = penguinpanel_intValue($params, 14, ['Public RDP Port', 'RDP Port'], 0);
        $fiveMExternal = penguinpanel_intValue($params, 15, ['Public FiveM Port', 'FiveM Public Port'], 0);

        if ($cpu <= 0 || $memory < 512 || $disk <= 0) {
            throw new Exception('Invalid VPS resources. CPU must be >0, memory at least 512 MB, and disk >0 GB.');
        }
        if (!in_array($generation, [1, 2], true)) {
            throw new Exception('Hyper-V Generation must be 1 or 2.');
        }
        if ($generation === 1) {
            $secureBoot = false;
        }
        if ($configureGuest && trim($templatePassword) === '') {
            throw new Exception('Template Administrator Password is required while Configure Windows Guest is enabled.');
        }

        $servicePassword = (string) ($params['password'] ?? '');
        if ($servicePassword === '') {
            $servicePassword = penguinpanel_generatePassword();
        }

        $clientEmail = trim((string) ($params['clientsdetails']['email'] ?? ''));
        $clientFirstName = trim((string) ($params['clientsdetails']['firstname'] ?? ''));
        $existingServiceUsername = trim((string) ($params['username'] ?? ''));

        // Friendly Hyper-V naming: use the WHMCS client's first name by default so
        // the host is easy to scan. A manually supplied configurable option named
        // "VM Name" still takes priority. PenguinPanel performs a second uniqueness
        // check and appends the WHMCS service id if that first-name VM already exists.
        $requestedVmName = trim((string) penguinpanel_configurableValue($params, ['VM Name'], ''));
        if ($requestedVmName !== '') {
            $vmName = penguinpanel_safeVmName($requestedVmName);
        } elseif ($clientFirstName !== '') {
            $vmName = penguinpanel_safeVmName($clientFirstName);
        } else {
            $vmName = penguinpanel_safeVmName('PH-VPS-' . $serviceId);
        }

        // Prefer the customer's WHMCS first name for a recognizable PenguinPanel
        // login without exposing their email address. PenguinPanel sanitizes the
        // value and automatically appends the WHMCS client id if the name is
        // already taken. On retries/older services, preserve an existing sensible
        // non-email service username when no first name is available.
        if ($clientFirstName !== '' && strpos($clientFirstName, '@') === false) {
            $customerUsername = $clientFirstName;
        } elseif ($existingServiceUsername !== ''
            && strcasecmp($existingServiceUsername, 'Administrator') !== 0
            && strpos($existingServiceUsername, '@') === false) {
            $customerUsername = $existingServiceUsername;
        } else {
            $customerUsername = 'penguin-' . $clientId;
        }

        $request = [
            'serviceId' => $serviceId,
            'clientId' => $clientId,
            'customerUsername' => $customerUsername,
            'customerEmail' => $clientEmail,
            // v1 intentionally uses the WHMCS service password for both accounts.
            // WHMCS already encrypts its service password; no plaintext side-table is needed.
            'panelTemporaryPassword' => $servicePassword,
            'vmName' => $vmName,
            'templateId' => $templateId,
            'cpuCores' => $cpu,
            'memoryMb' => $memory,
            'diskGb' => $disk,
            'generation' => $generation,
            'secureBoot' => $secureBoot,
            'startAfterProvision' => $startAfter,
            'configureWindowsGuest' => $configureGuest,
            'guestUsername' => $guestUsername !== '' ? $guestUsername : 'Administrator',
            'templatePassword' => $templatePassword,
            'newGuestPassword' => $servicePassword,
            'internalIpAddress' => '',
            'rdpExternalPort' => $rdpExternal > 0 ? $rdpExternal : null,
            'storageRoot' => $storageRoot,
            'enableFiveM' => $enableFiveM,
            'fiveMExternalPort' => ($enableFiveM && $fiveMExternal > 0) ? $fiveMExternal : null,
            'fiveMInternalPort' => $fiveMInternal > 0 ? $fiveMInternal : 30120,
        ];

        $client = penguinpanel_client($params);
        // VHDX cloning and guest configuration can take several minutes.
        $response = $client->post('/api/whmcs/vps/create', $request, 900);

        $returnedStatus = strtolower(trim((string) ($response['serviceStatus'] ?? '')));
        if ($returnedStatus !== 'active') {
            $message = trim((string) ($response['message'] ?? ''));
            if ($message === '') {
                $message = 'PenguinPanel returned service state ' . ($returnedStatus !== '' ? $returnedStatus : 'unknown') . ' after CreateAccount.';
            }
            throw new Exception($message);
        }

        penguinpanel_syncServiceProperties($params, $response, $servicePassword);
        penguinpanel_syncEmailCustomFields($params, $response);
        penguinpanel_saveResourceProperties($params, $response, $cpu, $memory, $disk);
        penguinpanel_log('CreateAccount', penguinpanel_sanitizeRequest($request), penguinpanel_sanitizeResponse($response));

        return 'success';
    } catch (Throwable $e) {
        penguinpanel_log('CreateAccount', ['serviceId' => $params['serviceid'] ?? null], ['error' => $e->getMessage()]);
        return $e->getMessage();
    }
}

function penguinpanel_SuspendAccount(array $params)
{
    return penguinpanel_serviceOperation($params, 'SuspendAccount', 'POST', '/suspend', 120);
}

function penguinpanel_UnsuspendAccount(array $params)
{
    return penguinpanel_serviceOperation($params, 'UnsuspendAccount', 'POST', '/unsuspend', 120);
}

function penguinpanel_TerminateAccount(array $params)
{
    return penguinpanel_serviceOperation($params, 'TerminateAccount', 'DELETE', '', 900);
}

function penguinpanel_ChangePackage(array $params)
{
    try {
        $serviceId = (int) ($params['serviceid'] ?? 0);
        if ($serviceId <= 0) {
            throw new Exception('WHMCS did not supply a valid service ID for the package change.');
        }

        $cpu = penguinpanel_intValue($params, 2, ['CPU Cores', 'CPU', 'vCPU', 'vCPUs'], 2);
        $memory = penguinpanel_intValue($params, 3, ['Memory MB', 'Memory', 'RAM', 'RAM MB'], 4096);
        $disk = penguinpanel_intValue($params, 4, ['Disk GB', 'Disk', 'Storage GB', 'Storage'], 128);

        if ($cpu <= 0 || $memory < 512 || $disk <= 0) {
            throw new Exception('Invalid upgraded VPS resources. CPU must be >0, memory at least 512 MB, and disk >0 GB.');
        }

        $request = [
            'cpuCores' => $cpu,
            'memoryMb' => $memory,
            'diskGb' => $disk,
        ];

        $response = penguinpanel_client($params)->post(
            '/api/whmcs/vps/' . $serviceId . '/resize',
            $request,
            900
        );

        penguinpanel_saveResourceProperties($params, $response, $cpu, $memory, $disk);
        penguinpanel_log('ChangePackage', $request, penguinpanel_sanitizeResponse($response));

        return 'success';
    } catch (Throwable $e) {
        penguinpanel_log('ChangePackage', ['serviceId' => $params['serviceid'] ?? null], ['error' => $e->getMessage()]);
        return $e->getMessage();
    }
}

function penguinpanel_ClientArea(array $params)
{
    $status = null;
    $error = '';

    try {
        $status = penguinpanel_getStatus($params);
        if ($status) {
            penguinpanel_syncServiceProperties($params, $status, (string) ($params['password'] ?? ''), false);
            penguinpanel_syncEmailCustomFields($params, $status);
        }
    } catch (Throwable $e) {
        $error = $e->getMessage();
    }

    $serviceId = (int) ($params['serviceid'] ?? 0);
    $publicAddress = $status['publicAddress'] ?? penguinpanel_property($params, 'Public IPv4', (string) ($params['dedicatedip'] ?? ''));
    $rdpHost = penguinpanel_customerRdpHostname($params, (string) penguinpanel_property($params, 'RDP Host', $publicAddress));
    $rdpPort = $status['rdpPort'] ?? penguinpanel_property($params, 'RDP Port', '');
    $internalIp = $status['internalIpAddress'] ?? penguinpanel_property($params, 'Internal IPv4', '');
    $fiveMPort = $status['fiveMPort'] ?? penguinpanel_property($params, 'FiveM Port', '');
    $clientFirstName = trim((string) ($params['clientsdetails']['firstname'] ?? ''));
    $savedServiceUsername = trim((string) ($params['username'] ?? ''));
    $panelUsernameFallback = $savedServiceUsername !== '' && strpos($savedServiceUsername, '@') === false
        ? $savedServiceUsername
        : ($clientFirstName !== '' ? $clientFirstName : 'penguin-' . (int) ($params['userid'] ?? 0));
    $panelUsername = penguinpanel_property($params, 'PenguinPanel Username', $panelUsernameFallback);
    $windowsUsername = penguinpanel_property($params, 'Windows Username', 'Administrator');
    $servicePassword = (string) ($params['password'] ?? '');
    $cpuCores = (int) penguinpanel_property($params, 'CPU Cores', penguinpanel_intValue($params, 2, ['CPU Cores', 'CPU', 'vCPU', 'vCPUs'], 2));
    $memoryMb = (int) penguinpanel_property($params, 'Memory MB', penguinpanel_intValue($params, 3, ['Memory MB', 'Memory', 'RAM', 'RAM MB'], 4096));
    $diskGb = (int) penguinpanel_property($params, 'Disk GB', penguinpanel_intValue($params, 4, ['Disk GB', 'Disk', 'Storage GB', 'Storage'], 128));

    $systemUrl = trim((string) ($params['systemurl'] ?? ''));
    if ($systemUrl === '' && class_exists('\\WHMCS\\Config\\Setting')) {
        try {
            $systemUrl = trim((string) \WHMCS\Config\Setting::getValue('SystemURL'));
        } catch (Throwable $ignored) {
            $systemUrl = '';
        }
    }
    $systemUrl = rtrim($systemUrl, '/');
    $upgradeUrl = ($systemUrl !== '' && $serviceId > 0)
        ? $systemUrl . '/upgrade.php?type=configoptions&id=' . $serviceId
        : '';
    $supportUrl = $systemUrl !== '' ? $systemUrl . '/submitticket.php' : '';

    return [
        'templatefile' => 'templates/clientarea',
        'vars' => [
            'ppServiceId' => $serviceId,
            'ppVmName' => $status['vmName'] ?? penguinpanel_property($params, 'PenguinPanel VM Name', ''),
            'ppServiceStatus' => $status['serviceStatus'] ?? (string) ($params['status'] ?? ''),
            'ppVmState' => $status['vmState'] ?? 'Unknown',
            'ppVmExists' => $status['vmExists'] ?? null,
            'ppPublicAddress' => (string) $publicAddress,
            'ppRdpPort' => (string) $rdpPort,
            'ppRdpEndpoint' => ($rdpHost && $rdpPort) ? $rdpHost . ':' . $rdpPort : '',
            'ppInternalIp' => (string) $internalIp,
            'ppFiveMPort' => (string) $fiveMPort,
            'ppWindowsUsername' => (string) $windowsUsername,
            'ppPanelUsername' => (string) $panelUsername,
            'ppCpuCores' => (int) $cpuCores,
            'ppMemoryMb' => (int) $memoryMb,
            'ppDiskGb' => (int) $diskGb,
            'ppUpgradeUrl' => $upgradeUrl,
            'ppSupportUrl' => $supportUrl,
            'ppServicePassword' => $servicePassword,
            'ppServicePasswordJson' => json_encode($servicePassword, JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT),
            'ppPanelUrl' => penguinpanel_baseUrl($params),
            'ppApiError' => $error,
            'ppLastError' => $status['lastError'] ?? '',
        ],
    ];
}

function penguinpanel_AdminServicesTabFields(array $params)
{
    try {
        $status = penguinpanel_getStatus($params);
        $storageField = penguinpanel_adminStorageField($params, $status);
        if (!$status) {
            return [
                'VM Storage Selection' => $storageField,
                'PenguinPanel' => '<span style="color:#b7791f">Not provisioned yet — choose storage, save the service, then use Module Commands → Create.</span>',
            ];
        }

        penguinpanel_syncEmailCustomFields($params, $status);

        $rdp = penguinpanel_customerRdpHostname($params, trim((string) ($status['publicAddress'] ?? '')));
        if (!empty($status['rdpPort'])) {
            $rdp .= ':' . (int) $status['rdpPort'];
        }

        return [
            'VM Storage Selection' => $storageField,
            'Provisioned Storage Root' => htmlspecialchars((string) penguinpanel_property($params, 'VM Storage Root', ''), ENT_QUOTES, 'UTF-8'),
            'vCPU Cores' => htmlspecialchars((string) penguinpanel_property($params, 'CPU Cores', penguinpanel_intValue($params, 2, ['CPU Cores', 'CPU', 'vCPU', 'vCPUs'], 2)), ENT_QUOTES, 'UTF-8'),
            'Memory' => htmlspecialchars((string) penguinpanel_property($params, 'Memory MB', penguinpanel_intValue($params, 3, ['Memory MB', 'Memory', 'RAM', 'RAM MB'], 4096)) . ' MB', ENT_QUOTES, 'UTF-8'),
            'Disk Size' => htmlspecialchars((string) penguinpanel_property($params, 'Disk GB', penguinpanel_intValue($params, 4, ['Disk GB', 'Disk', 'Storage GB', 'Storage'], 128)) . ' GB', ENT_QUOTES, 'UTF-8'),
            'PenguinPanel VM' => htmlspecialchars((string) ($status['vmName'] ?? ''), ENT_QUOTES, 'UTF-8'),
            'PenguinPanel Login' => htmlspecialchars((string) penguinpanel_property($params, 'PenguinPanel Username', (string) ($params['username'] ?? '')), ENT_QUOTES, 'UTF-8'),
            'Windows Login' => htmlspecialchars((string) penguinpanel_property($params, 'Windows Username', 'Administrator'), ENT_QUOTES, 'UTF-8'),
            'Service State' => htmlspecialchars((string) ($status['serviceStatus'] ?? ''), ENT_QUOTES, 'UTF-8'),
            'Hyper-V State' => htmlspecialchars((string) ($status['vmState'] ?? ''), ENT_QUOTES, 'UTF-8'),
            'RDP Endpoint' => htmlspecialchars($rdp, ENT_QUOTES, 'UTF-8'),
            'Internal IPv4' => htmlspecialchars((string) ($status['internalIpAddress'] ?? ''), ENT_QUOTES, 'UTF-8'),
            'FiveM Public Port' => htmlspecialchars((string) ($status['fiveMPort'] ?? ''), ENT_QUOTES, 'UTF-8'),
            'PenguinPanel Last Error' => htmlspecialchars((string) ($status['lastError'] ?? ''), ENT_QUOTES, 'UTF-8'),
        ];
    } catch (Throwable $e) {
        return ['PenguinPanel API' => '<span style="color:#b91c1c">' . htmlspecialchars($e->getMessage(), ENT_QUOTES, 'UTF-8') . '</span>'];
    }
}

function penguinpanel_AdminServicesTabFieldsSave(array $params)
{
    if (!isset($_POST['modulefields']) || !is_array($_POST['modulefields']) || !array_key_exists(0, $_POST['modulefields'])) {
        return;
    }

    $selection = trim((string) $_POST['modulefields'][0]);
    $catalog = penguinpanel_client($params)->get('/api/whmcs/catalog', 30);
    $allowed = [];
    foreach (($catalog['storageRoots'] ?? []) as $root) {
        $root = trim((string) $root);
        if ($root !== '') {
            $allowed[] = $root;
        }
    }

    $stored = '__AUTO__';
    if ($selection !== '' && $selection !== '__AUTO__') {
        $matched = null;
        foreach ($allowed as $root) {
            if (strcasecmp($selection, $root) === 0) {
                $matched = $root;
                break;
            }
        }
        if ($matched === null) {
            throw new Exception('The selected VM storage root is not approved by PenguinPanel.');
        }
        $stored = $matched;
    }

    if (isset($params['model']) && is_object($params['model']) && $params['model']->serviceProperties) {
        $params['model']->serviceProperties->save(['VM Storage Root Override' => $stored]);
    }
}

function penguinpanel_adminStorageField(array $params, $status = null)
{
    $vmExists = is_array($status) && !empty($status['vmExists']);
    $provisionedRoot = trim((string) penguinpanel_property($params, 'VM Storage Root', ''));
    if ($vmExists) {
        $text = $provisionedRoot !== '' ? $provisionedRoot : 'Already provisioned';
        return '<strong>' . htmlspecialchars($text, ENT_QUOTES, 'UTF-8') . '</strong><br><small>Storage cannot be moved by changing this field after the VM exists.</small>';
    }

    $catalog = penguinpanel_client($params)->get('/api/whmcs/catalog', 30);
    $diskGb = penguinpanel_intValue($params, 4, ['Disk GB', 'Disk', 'Storage GB', 'Storage'], 128);
    $reserveGb = max(0, (int) ($catalog['minimumFreeSpaceAfterProvisionGb'] ?? 0));
    $reservePercent = max(0, (int) ($catalog['minimumFreeSpaceAfterProvisionPercent'] ?? 0));
    $saved = trim((string) penguinpanel_property($params, 'VM Storage Root Override', ''));
    if ($saved === '') {
        $productSelection = trim((string) penguinpanel_value($params, 11, ['Storage Root', 'VM Storage Root'], ''));
        $saved = $productSelection !== '' ? $productSelection : '__AUTO__';
    }
    $recommended = penguinpanel_recommendedStorageRoot($catalog, $diskGb);

    $html = '<select name="modulefields[0]" class="form-control select-inline" style="min-width:420px">';
    $autoLabel = 'Automatic — safest available drive';
    if ($recommended !== '') {
        $autoLabel .= ' — recommended: ' . $recommended;
    }
    $html .= '<option value="__AUTO__"' . ($saved === '__AUTO__' ? ' selected' : '') . '>' . htmlspecialchars($autoLabel, ENT_QUOTES, 'UTF-8') . '</option>';

    $details = $catalog['storageRootDetails'] ?? [];
    if (is_array($details) && $details) {
        foreach ($details as $item) {
            if (!is_array($item)) {
                continue;
            }
            $root = trim((string) ($item['path'] ?? ''));
            if ($root === '') {
                continue;
            }
            $available = !empty($item['isAvailable']);
            $canFit = $available && penguinpanel_storageCanFit($item, $diskGb, $reserveGb, $reservePercent);
            $label = $root;
            if ($available) {
                $label .= ' — ' . penguinpanel_formatBytes($item['freeBytes'] ?? 0) . ' free';
                if (!empty($item['isDefault'])) {
                    $label .= ' — Default';
                }
                if (!$canFit) {
                    $label .= ' — NOT ENOUGH SPACE for ' . $diskGb . ' GB + reserve';
                }
            } else {
                $label .= ' — Unavailable';
            }
            $selected = strcasecmp($saved, $root) === 0 ? ' selected' : '';
            $disabled = $canFit ? '' : ' disabled';
            $html .= '<option value="' . htmlspecialchars($root, ENT_QUOTES, 'UTF-8') . '"' . $selected . $disabled . '>' . htmlspecialchars($label, ENT_QUOTES, 'UTF-8') . '</option>';
        }
    } else {
        foreach (($catalog['storageRoots'] ?? []) as $root) {
            $root = trim((string) $root);
            if ($root === '') {
                continue;
            }
            $selected = strcasecmp($saved, $root) === 0 ? ' selected' : '';
            $html .= '<option value="' . htmlspecialchars($root, ENT_QUOTES, 'UTF-8') . '"' . $selected . '>' . htmlspecialchars($root, ENT_QUOTES, 'UTF-8') . '</option>';
        }
    }
    $html .= '</select>';
    $html .= '<br><small>Requested disk: ' . (int) $diskGb . ' GB. PenguinPanel re-checks free space at Create time. Save Changes after selecting a drive.</small>';
    return $html;
}

function penguinpanel_AdminLink(array $params)
{
    $url = htmlspecialchars(penguinpanel_baseUrl($params), ENT_QUOTES, 'UTF-8');
    return '<a href="' . $url . '" target="_blank" rel="noopener">Open PenguinPanel</a>';
}

function penguinpanel_serviceOperation(array $params, $action, $method, $suffix, $timeout)
{
    try {
        $serviceId = (int) ($params['serviceid'] ?? 0);
        if ($serviceId <= 0) {
            throw new Exception('Invalid WHMCS service ID.');
        }

        $path = '/api/whmcs/vps/' . $serviceId . $suffix;
        $client = penguinpanel_client($params);
        if ($method === 'DELETE') {
            $response = $client->delete($path, $timeout);
        } else {
            $response = $client->post($path, null, $timeout);
        }

        penguinpanel_log($action, ['serviceId' => $serviceId], penguinpanel_sanitizeResponse($response));
        return 'success';
    } catch (Throwable $e) {
        penguinpanel_log($action, ['serviceId' => $params['serviceid'] ?? null], ['error' => $e->getMessage()]);
        return $e->getMessage();
    }
}

function penguinpanel_getStatus(array $params)
{
    $serviceId = (int) ($params['serviceid'] ?? 0);
    if ($serviceId <= 0) {
        return null;
    }

    try {
        return penguinpanel_client($params)->get('/api/whmcs/vps/' . $serviceId, 45);
    } catch (PenguinPanelApiException $e) {
        // A new WHMCS service does not exist in PenguinPanel until CreateAccount
        // has successfully run. Treat that specific 404 as a normal
        // "not provisioned yet" state instead of showing it as an API failure.
        if ($e->getStatusCode() === 404) {
            return null;
        }
        throw $e;
    }
}

function penguinpanel_client(array $params)
{
    return new PenguinPanelApiClient(penguinpanel_baseUrl($params), penguinpanel_apiKey($params));
}

function penguinpanel_baseUrl(array $params)
{
    $rawHost = trim((string) ($params['serverhostname'] ?? ''));
    if ($rawHost === '') {
        $rawHost = trim((string) ($params['serverip'] ?? ''));
    }
    if ($rawHost === '') {
        throw new Exception('Set the PenguinPanel server Hostname in WHMCS.');
    }

    if (preg_match('#^https?://#i', $rawHost)) {
        $parts = parse_url($rawHost);
        if (!$parts || empty($parts['host'])) {
            throw new Exception('The PenguinPanel server Hostname/URL is invalid.');
        }
        $scheme = strtolower((string) ($parts['scheme'] ?? 'https'));
        $host = $parts['host'];
        $port = isset($parts['port']) ? (int) $parts['port'] : 0;
        $path = rtrim((string) ($parts['path'] ?? ''), '/');
    } else {
        $scheme = !empty($params['serversecure']) ? 'https' : 'http';
        $host = preg_replace('#/.*$#', '', $rawHost);
        $port = (int) ($params['serverport'] ?? 0);
        $path = '';
    }

    if ($port <= 0) {
        $port = $scheme === 'https' ? 443 : 80;
    }
    $portPart = (($scheme === 'https' && $port === 443) || ($scheme === 'http' && $port === 80)) ? '' : ':' . $port;

    return $scheme . '://' . $host . $portPart . $path;
}

function penguinpanel_apiKey(array $params)
{
    $key = trim((string) ($params['serverpassword'] ?? ''));
    if ($key === '') {
        $key = trim((string) ($params['serveraccesshash'] ?? ''));
    }
    if ($key === '') {
        throw new Exception('Put the plaintext PenguinPanel API key in the WHMCS server Password field.');
    }
    return $key;
}

function penguinpanel_value(array $params, $index, array $aliases, $default = null)
{
    $override = penguinpanel_configurableValue($params, $aliases, null);
    if ($override !== null && $override !== '') {
        return $override;
    }

    $key = 'configoption' . (int) $index;
    if (array_key_exists($key, $params) && $params[$key] !== '') {
        return $params[$key];
    }

    return $default;
}

function penguinpanel_configurableValue(array $params, array $aliases, $default = null)
{
    $options = $params['configoptions'] ?? [];
    if (!is_array($options)) {
        return $default;
    }

    foreach ($aliases as $alias) {
        foreach ($options as $name => $value) {
            if (strcasecmp(trim((string) $name), trim((string) $alias)) === 0) {
                return $value;
            }
        }
    }

    return $default;
}

function penguinpanel_intValue(array $params, $index, array $aliases, $default)
{
    $value = penguinpanel_value($params, $index, $aliases, $default);
    if (is_int($value) || is_float($value)) {
        return (int) $value;
    }
    if (preg_match('/-?\d+/', (string) $value, $m)) {
        return (int) $m[0];
    }
    return (int) $default;
}

function penguinpanel_boolValue(array $params, $index, array $aliases, $default)
{
    $value = penguinpanel_value($params, $index, $aliases, $default ? 'on' : '');
    if (is_bool($value)) {
        return $value;
    }
    $normalized = strtolower(trim((string) $value));
    return in_array($normalized, ['1', 'true', 'yes', 'on', 'enabled'], true);
}

function penguinpanel_safeVmName($name)
{
    // Keep Unicode letters/numbers so Swedish names such as Åke/Örjan remain
    // recognizable, while replacing filesystem-unfriendly punctuation with '-'.
    $name = preg_replace('/[^\p{L}\p{N}._-]+/u', '-', trim((string) $name));
    $name = trim($name, '-.');
    if ($name === '') {
        throw new Exception('Could not create a valid Hyper-V VM name.');
    }
    if (strlen($name) > 100) {
        $name = substr($name, 0, 100);
    }
    return $name;
}

function penguinpanel_generatePassword()
{
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@$%';
    $bytes = random_bytes(20);
    $password = '';
    $length = strlen($alphabet);
    foreach (str_split($bytes) as $byte) {
        $password .= $alphabet[ord($byte) % $length];
    }
    return $password;
}

function penguinpanel_customerRdpHostname(array $params, $fallback = '')
{
    $value = trim((string) penguinpanel_value(
        $params,
        16,
        ['RDP Customer Hostname', 'Customer RDP Hostname'],
        'remotedesktop.penguinhosting.host'
    ));

    // v1.2.2 briefly used configoption16 for the old VM prefix. Existing
    // products may therefore still contain PH-VPS- until they are saved once.
    // Treat that legacy value as unset instead of ever exposing it as a host.
    if ($value === '' || strcasecmp($value, 'PH-VPS-') === 0) {
        $value = 'remotedesktop.penguinhosting.host';
    }

    // This is a display/connection hostname, never a URL.
    $value = preg_replace('~^https?://~i', '', $value);
    $value = trim((string) $value, " \t\n\r\0\x0B/");
    if (strpos($value, '/') !== false) {
        $value = explode('/', $value, 2)[0];
    }

    return $value !== '' ? $value : trim((string) $fallback);
}

function penguinpanel_syncEmailCustomFields(array $params, array $data)
{
    $productId = (int) ($params['pid'] ?? 0);
    $serviceId = (int) ($params['serviceid'] ?? 0);
    if ($productId <= 0 || $serviceId <= 0 || !class_exists('\\WHMCS\\Database\\Capsule')) {
        return;
    }

    $publicIp = trim((string) ($data['publicAddress']
        ?? penguinpanel_property($params, 'Public IPv4', (string) ($params['dedicatedip'] ?? ''))));

    $values = [
        'PenguinPanel VM Name' => (string) ($data['vmName'] ?? penguinpanel_property($params, 'PenguinPanel VM Name', '')),
        'RDP Host' => penguinpanel_customerRdpHostname($params, $publicIp),
        'RDP Port' => (string) ($data['rdpPort'] ?? penguinpanel_property($params, 'RDP Port', '')),
        'Windows Username' => (string) ($data['guestUsername'] ?? penguinpanel_property($params, 'Windows Username', 'Administrator')),
        'Internal IPv4' => (string) ($data['internalIpAddress'] ?? penguinpanel_property($params, 'Internal IPv4', '')),
        'Public IPv4' => $publicIp,
        'FiveM Port' => (string) ($data['fiveMPort'] ?? penguinpanel_property($params, 'FiveM Port', '')),
        'FiveM Internal Port' => (string) ($data['fiveMInternalPort'] ?? penguinpanel_property($params, 'FiveM Internal Port', '')),
    ];

    try {
        foreach ($values as $fieldName => $fieldValue) {
            $fieldId = (int) \WHMCS\Database\Capsule::table('tblcustomfields')
                ->where('type', 'product')
                ->where('relid', $productId)
                ->where('fieldname', $fieldName)
                ->value('id');

            if ($fieldId <= 0) {
                $fieldId = (int) \WHMCS\Database\Capsule::table('tblcustomfields')->insertGetId([
                    'type' => 'product',
                    'relid' => $productId,
                    'fieldname' => $fieldName,
                    'fieldtype' => 'text',
                    'description' => 'Managed automatically by PenguinPanel for WHMCS emails.',
                    'fieldoptions' => '',
                    'regexpr' => '',
                    'adminonly' => 'on',
                    'required' => '',
                    'showorder' => '',
                    'showinvoice' => '',
                    'sortorder' => 0,
                ]);
            }

            if ($fieldId <= 0) {
                continue;
            }

            $existing = \WHMCS\Database\Capsule::table('tblcustomfieldsvalues')
                ->where('fieldid', $fieldId)
                ->where('relid', $serviceId)
                ->first();

            if ($existing) {
                \WHMCS\Database\Capsule::table('tblcustomfieldsvalues')
                    ->where('fieldid', $fieldId)
                    ->where('relid', $serviceId)
                    ->update(['value' => (string) $fieldValue]);
            } else {
                \WHMCS\Database\Capsule::table('tblcustomfieldsvalues')->insert([
                    'fieldid' => $fieldId,
                    'relid' => $serviceId,
                    'value' => (string) $fieldValue,
                ]);
            }
        }
    } catch (Throwable $e) {
        // Provisioning must never fail only because an email mirror field could not be updated.
        penguinpanel_log('EmailCustomFields', ['serviceId' => $serviceId], ['error' => $e->getMessage()]);
    }
}

function penguinpanel_syncServiceProperties(array $params, array $data, $servicePassword, $includeCredentials = true)
{
    if (!isset($params['model']) || !is_object($params['model'])) {
        return;
    }

    try {
        $serviceProperties = $params['model']->serviceProperties;
    } catch (Throwable $e) {
        return;
    }
    if (!$serviceProperties) {
        return;
    }

    $properties = [
        'PenguinPanel Service ID' => (string) ($data['serviceId'] ?? ($params['serviceid'] ?? '')),
        'PenguinPanel VM Name' => (string) ($data['vmName'] ?? ''),
        'PenguinPanel Username' => (string) ($data['panelUsername'] ?? penguinpanel_property($params, 'PenguinPanel Username', '')),
        'PenguinPanel Account Created' => !empty($data['panelAccountCreated']) ? 'Yes' : penguinpanel_property($params, 'PenguinPanel Account Created', 'No'),
        'RDP Host' => penguinpanel_customerRdpHostname($params, (string) ($data['publicAddress'] ?? '')),
        'RDP Port' => (string) ($data['rdpPort'] ?? ''),
        'Public IPv4' => (string) ($data['publicAddress'] ?? penguinpanel_property($params, 'Public IPv4', (string) ($params['dedicatedip'] ?? ''))),
        'Internal IPv4' => (string) ($data['internalIpAddress'] ?? ''),
        'VM Storage Root' => (string) ($data['storageRoot'] ?? penguinpanel_property($params, 'VM Storage Root', '')),
        'FiveM Port' => (string) ($data['fiveMPort'] ?? ''),
        'FiveM Internal Port' => (string) ($data['fiveMInternalPort'] ?? ''),
        'PenguinPanel Service Status' => (string) ($data['serviceStatus'] ?? ''),
        'PenguinPanel VM State' => (string) ($data['vmState'] ?? ''),
        'PenguinPanel Last Error' => (string) ($data['lastError'] ?? ''),
    ];

    if ($includeCredentials) {
        // WHMCS' primary service Username should be the control-panel login,
        // not the Windows Administrator account. Keep Windows credentials in
        // their own service property so emails/client-area output can show both.
        $panelUsername = (string) ($data['panelUsername'] ?? penguinpanel_property($params, 'PenguinPanel Username', ''));
        if ($panelUsername !== '') {
            $properties['Username'] = $panelUsername;
        }
        $properties['Windows Username'] = (string) ($data['guestUsername'] ?? 'Administrator');
        $properties['Password'] = (string) ($data['guestPassword'] ?? $servicePassword);
        if (!empty($data['publicAddress'])) {
            $properties['Dedicated IP'] = (string) $data['publicAddress'];
        }
    }

    $serviceProperties->save($properties);
}

function penguinpanel_saveResourceProperties(array $params, array $data, $cpu, $memory, $disk)
{
    if (!isset($params['model']) || !is_object($params['model'])) {
        return;
    }

    try {
        $serviceProperties = $params['model']->serviceProperties;
    } catch (Throwable $e) {
        return;
    }
    if (!$serviceProperties) {
        return;
    }

    $warnings = $data['warnings'] ?? [];
    if (!is_array($warnings)) {
        $warnings = [];
    }

    $serviceProperties->save([
        'CPU Cores' => (string) ($data['newCpuCores'] ?? $cpu),
        'Memory MB' => (string) ($data['newMemoryMb'] ?? $memory),
        'Disk GB' => (string) ($data['newDiskGb'] ?? $disk),
        'PenguinPanel VM State' => (string) ($data['vmState'] ?? penguinpanel_property($params, 'PenguinPanel VM State', '')),
        'PenguinPanel Last Error' => $warnings ? implode(' | ', array_map('strval', $warnings)) : '',
    ]);
}

function penguinpanel_property(array $params, $name, $default = '')
{
    try {
        if (isset($params['model']) && is_object($params['model'])) {
            $serviceProperties = $params['model']->serviceProperties;
            if ($serviceProperties) {
                $value = $serviceProperties->get($name);
                if ($value !== null && $value !== '') {
                    return $value;
                }
            }
        }
    } catch (Throwable $e) {
        // Fallback below.
    }

    if ($name === 'Username' && isset($params['username'])) {
        return $params['username'];
    }
    if ($name === 'Password' && isset($params['password'])) {
        return $params['password'];
    }

    return $default;
}

function penguinpanel_sanitizeRequest(array $request)
{
    $safe = $request;
    foreach (['templatePassword', 'newGuestPassword', 'panelTemporaryPassword'] as $secret) {
        if (array_key_exists($secret, $safe)) {
            $safe[$secret] = '***REDACTED***';
        }
    }
    return $safe;
}

function penguinpanel_sanitizeResponse(array $response)
{
    $safe = $response;
    foreach (['guestPassword', 'panelTemporaryPassword'] as $secret) {
        if (array_key_exists($secret, $safe)) {
            $safe[$secret] = '***REDACTED***';
        }
    }
    return $safe;
}

function penguinpanel_log($action, $request, $response)
{
    if (function_exists('logModuleCall')) {
        logModuleCall('penguinpanel', $action, $request, $response);
    }
}
