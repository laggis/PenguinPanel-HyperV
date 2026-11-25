using System.Collections.Concurrent;
using System.Management;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

#pragma warning disable CA1416 // PenguinPanel is a Windows/Hyper-V host application.

namespace HyperVPanel.Services;

/// <summary>
/// Boot-level Hyper-V console access using Microsoft's virtualization WMI provider.
/// This does not depend on guest networking, RDP, WinRM, PowerShell Direct, or a guest agent.
/// </summary>
public sealed class HyperVConsoleService(IConfiguration configuration)
{
    private const string HyperVNamespace = @"\\.\root\virtualization\v2";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _vmLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _vmPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedConsoleImage> _frameCache =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _managementServicePath;

    public async Task<HyperVConsoleFrame> CaptureFrameAsync(string vmName)
    {
        vmName = NormalizeVmName(vmName);
        var gate = _vmLocks.GetOrAdd(vmName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try
        {
            return await Task.Run(() => CaptureFrame(vmName));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task TypeTextAsync(string vmName, string text)
    {
        vmName = NormalizeVmName(vmName);
        text ??= string.Empty;

        if (text.Length == 0)
        {
            return;
        }

        // Microsoft's Msvm_Keyboard TypeText supports hundreds of characters,
        // but interactive browser typing should stay intentionally small.
        if (text.Length > 64)
        {
            text = text[..64];
        }

        await WithVmLockAsync(vmName, () =>
        {
            var scope = OpenScope();
            using var vm = GetVm(scope, vmName);
            using var keyboard = GetRelatedObject(
                vm,
                "Msvm_Keyboard",
                "Msvm_SystemDevice",
                "PartComponent",
                "GroupComponent");

            using var input = keyboard.GetMethodParameters("TypeText");
            input["asciiText"] = text;
            using var output = keyboard.InvokeMethod("TypeText", input, null);
            EnsureCompleted(output, "TypeText");
        });
    }

    public async Task TypeKeyAsync(string vmName, uint virtualKeyCode)
    {
        vmName = NormalizeVmName(vmName);
        if (virtualKeyCode > 255)
        {
            throw new InvalidOperationException("Virtual key code must be between 0 and 255.");
        }

        await WithVmLockAsync(vmName, () =>
        {
            var scope = OpenScope();
            using var vm = GetVm(scope, vmName);
            using var keyboard = GetRelatedObject(
                vm,
                "Msvm_Keyboard",
                "Msvm_SystemDevice",
                "PartComponent",
                "GroupComponent");

            InvokeKeyboardKeyMethod(keyboard, "TypeKey", virtualKeyCode);
        });
    }

    public async Task SendKeyChordAsync(
        string vmName,
        uint virtualKeyCode,
        bool control,
        bool alt,
        bool shift)
    {
        vmName = NormalizeVmName(vmName);
        if (virtualKeyCode > 255)
        {
            throw new InvalidOperationException("Virtual key code must be between 0 and 255.");
        }

        await WithVmLockAsync(vmName, () =>
        {
            var scope = OpenScope();
            using var vm = GetVm(scope, vmName);
            using var keyboard = GetRelatedObject(
                vm,
                "Msvm_Keyboard",
                "Msvm_SystemDevice",
                "PartComponent",
                "GroupComponent");

            const uint vkControl = 0x11;
            const uint vkAlt = 0x12;
            const uint vkShift = 0x10;
            var pressed = new List<uint>(3);

            try
            {
                if (control)
                {
                    InvokeKeyboardKeyMethod(keyboard, "PressKey", vkControl);
                    pressed.Add(vkControl);
                }
                if (alt)
                {
                    InvokeKeyboardKeyMethod(keyboard, "PressKey", vkAlt);
                    pressed.Add(vkAlt);
                }
                if (shift)
                {
                    InvokeKeyboardKeyMethod(keyboard, "PressKey", vkShift);
                    pressed.Add(vkShift);
                }

                InvokeKeyboardKeyMethod(keyboard, "TypeKey", virtualKeyCode);
            }
            finally
            {
                for (var index = pressed.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        InvokeKeyboardKeyMethod(keyboard, "ReleaseKey", pressed[index]);
                    }
                    catch
                    {
                        // Keep releasing remaining modifiers so one failed release
                        // does not leave the guest keyboard logically stuck.
                    }
                }
            }
        });
    }

    public async Task SendCtrlAltDeleteAsync(string vmName)
    {
        vmName = NormalizeVmName(vmName);

        await WithVmLockAsync(vmName, () =>
        {
            var scope = OpenScope();
            using var vm = GetVm(scope, vmName);
            using var keyboard = GetRelatedObject(
                vm,
                "Msvm_Keyboard",
                "Msvm_SystemDevice",
                "PartComponent",
                "GroupComponent");

            const uint vkControl = 0x11;
            const uint vkAlt = 0x12;
            const uint vkDelete = 0x2E;

            InvokeKeyboardKeyMethod(keyboard, "PressKey", vkControl);
            try
            {
                InvokeKeyboardKeyMethod(keyboard, "PressKey", vkAlt);
                try
                {
                    InvokeKeyboardKeyMethod(keyboard, "TypeKey", vkDelete);
                }
                finally
                {
                    InvokeKeyboardKeyMethod(keyboard, "ReleaseKey", vkAlt);
                }
            }
            finally
            {
                InvokeKeyboardKeyMethod(keyboard, "ReleaseKey", vkControl);
            }
        });
    }

    public async Task ClickAsync(string vmName, double normalizedX, double normalizedY, uint buttonIndex)
    {
        vmName = NormalizeVmName(vmName);
        normalizedX = Math.Clamp(normalizedX, 0d, 1d);
        normalizedY = Math.Clamp(normalizedY, 0d, 1d);
        buttonIndex = buttonIndex is >= 1 and <= 5 ? buttonIndex : 1;

        await WithVmLockAsync(vmName, () =>
        {
            var scope = OpenScope();
            using var vm = GetVm(scope, vmName);
            using var videoHead = TryGetVideoHead(vm);
            using var mouse = GetRelatedObject(
                vm,
                "Msvm_SyntheticMouse",
                "Msvm_SystemDevice",
                "PartComponent",
                "GroupComponent");

            // Mouse position needs the VM's current drawing-surface dimensions.
            // During very early firmware/boot stages Hyper-V might not expose a
            // video-head object yet, so fall back to the configured console size.
            var width = videoHead is null
                ? Math.Clamp(configuration.GetValue("Console:HyperV:MaxWidth", 800), 320, 1920)
                : SafePositiveInt(videoHead["CurrentHorizontalResolution"], 800);
            var height = videoHead is null
                ? Math.Clamp(configuration.GetValue("Console:HyperV:MaxHeight", 600), 240, 1200)
                : SafePositiveInt(videoHead["CurrentVerticalResolution"], 600);
            var x = (int)Math.Round(normalizedX * Math.Max(0, width - 1));
            var y = (int)Math.Round(normalizedY * Math.Max(0, height - 1));

            using (var move = mouse.GetMethodParameters("SetAbsolutePosition"))
            {
                move["horizontalPosition"] = x;
                move["verticalPosition"] = y;
                using var result = mouse.InvokeMethod("SetAbsolutePosition", move, null);
                EnsureCompleted(result, "SetAbsolutePosition");
            }

            using var click = mouse.GetMethodParameters("ClickButton");
            click["buttonIndex"] = buttonIndex;
            using var clickResult = mouse.InvokeMethod("ClickButton", click, null);
            EnsureCompleted(clickResult, "ClickButton");
        });
    }

    private HyperVConsoleFrame CaptureFrame(string vmName)
    {
        EnsureWindows();

        var scope = OpenScope();
        using var vm = GetVm(scope, vmName);

        var maxWidth = Math.Clamp(configuration.GetValue("Console:HyperV:MaxWidth", 800), 320, 1920);
        var maxHeight = Math.Clamp(configuration.GetValue("Console:HyperV:MaxHeight", 600), 240, 1200);

        // GetVirtualSystemThumbnailImage itself does not require a guest display
        // driver and is the authoritative framebuffer source. Video-head lookup
        // is optional and is used only to preserve the VM's current aspect ratio
        // and report its native resolution. This is important for firmware/boot
        // screens where the synthetic display controller may not yet be active.
        using var videoHead = TryGetVideoHead(vm);
        var virtualWidth = videoHead is null
            ? maxWidth
            : SafePositiveInt(videoHead["CurrentHorizontalResolution"], maxWidth);
        var virtualHeight = videoHead is null
            ? maxHeight
            : SafePositiveInt(videoHead["CurrentVerticalResolution"], maxHeight);

        var scale = Math.Min(
            1d,
            Math.Min(
                maxWidth / (double)Math.Max(1, virtualWidth),
                maxHeight / (double)Math.Max(1, virtualHeight)));

        var width = Math.Max(16, (int)Math.Round(virtualWidth * scale));
        var height = Math.Max(16, (int)Math.Round(virtualHeight * scale));
        width = Math.Min(width, ushort.MaxValue);
        height = Math.Min(height, ushort.MaxValue);

        using var settingData = GetRelatedObject(
            vm,
            "Msvm_VirtualSystemSettingData",
            "Msvm_SettingsDefineState",
            "SettingData",
            "ManagedElement");
        using var managementService = GetManagementService(scope);
        using var input = managementService.GetMethodParameters("GetVirtualSystemThumbnailImage");

        input["HeightPixels"] = (ushort)height;
        input["WidthPixels"] = (ushort)width;
        input["TargetSystem"] = settingData.Path.Path;

        using var output = managementService.InvokeMethod(
            "GetVirtualSystemThumbnailImage",
            input,
            null);

        EnsureCompleted(output, "GetVirtualSystemThumbnailImage", scope);

        var imageData = output?["ImageData"] as byte[]
            ?? throw new InvalidOperationException(
                "Hyper-V returned no framebuffer image. The VM may still be starting; try again in a moment.");

        var expectedBytes = checked(width * height * 2);
        if (imageData.Length < expectedBytes)
        {
            throw new InvalidOperationException(
                $"Hyper-V returned an incomplete framebuffer ({imageData.Length} bytes; expected at least {expectedBytes}).");
        }

        var flipVertical = configuration.GetValue("Console:HyperV:FlipVertical", false);
        var pixelHash = ComputeFrameHash(imageData.AsSpan(0, expectedBytes));

        string dataUri;
        if (_frameCache.TryGetValue(vmName, out var cached)
            && cached.PixelHash == pixelHash
            && cached.Width == width
            && cached.Height == height
            && cached.FlipVertical == flipVertical)
        {
            // The VM framebuffer is unchanged. Reusing the same src string lets
            // Blazor avoid re-sending/re-decoding a large BMP while still updating
            // the console heartbeat/status timestamp.
            dataUri = cached.DataUri;
        }
        else
        {
            var png = Rgb565ToPng24Fast(imageData, width, height, flipVertical);
            dataUri = "data:image/png;base64," + Convert.ToBase64String(png);
            _frameCache[vmName] = new CachedConsoleImage(
                pixelHash,
                width,
                height,
                flipVertical,
                dataUri);
        }

        return new HyperVConsoleFrame(
            vmName,
            dataUri,
            width,
            height,
            virtualWidth,
            virtualHeight,
            DateTime.UtcNow);
    }

    private async Task WithVmLockAsync(string vmName, Action action)
    {
        var gate = _vmLocks.GetOrAdd(vmName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try
        {
            await Task.Run(action);
        }
        finally
        {
            gate.Release();
        }
    }

    private static ManagementScope OpenScope()
    {
        EnsureWindows();
        var scope = new ManagementScope(HyperVNamespace);
        scope.Connect();
        return scope;
    }

    private ManagementObject GetVm(ManagementScope scope, string vmName)
    {
        if (_vmPaths.TryGetValue(vmName, out var cachedPath))
        {
            try
            {
                var cachedVm = new ManagementObject(
                    scope,
                    new ManagementPath(cachedPath),
                    null);
                cachedVm.Get();

                if (string.Equals(
                        cachedVm["ElementName"]?.ToString(),
                        vmName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return cachedVm;
                }

                cachedVm.Dispose();
            }
            catch
            {
                // VM may have been deleted/recreated. Fall back to discovery.
            }

            _vmPaths.TryRemove(vmName, out _);
            _frameCache.TryRemove(vmName, out _);
        }

        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT * FROM Msvm_ComputerSystem WHERE Caption = 'Virtual Machine'"));

        using var results = searcher.Get();
        foreach (ManagementObject candidate in results)
        {
            try
            {
                if (string.Equals(
                        candidate["ElementName"]?.ToString(),
                        vmName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var result = new ManagementObject(
                        scope,
                        new ManagementPath(candidate.Path.Path),
                        null);
                    result.Get();

                    _vmPaths[vmName] = result.Path.Path;
                    return result;
                }
            }
            finally
            {
                candidate.Dispose();
            }
        }

        _vmPaths.TryRemove(vmName, out _);
        _frameCache.TryRemove(vmName, out _);
        throw new KeyNotFoundException($"Hyper-V VM '{vmName}' was not found.");
    }

    private ManagementObject GetManagementService(ManagementScope scope)
    {
        var cachedPath = _managementServicePath;
        if (!string.IsNullOrWhiteSpace(cachedPath))
        {
            try
            {
                var cachedService = new ManagementObject(
                    scope,
                    new ManagementPath(cachedPath),
                    null);
                cachedService.Get();
                return cachedService;
            }
            catch
            {
                _managementServicePath = null;
            }
        }

        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT * FROM Msvm_VirtualSystemManagementService"));

        using var results = searcher.Get();
        foreach (ManagementObject candidate in results)
        {
            try
            {
                var result = new ManagementObject(
                    scope,
                    new ManagementPath(candidate.Path.Path),
                    null);
                result.Get();

                _managementServicePath = result.Path.Path;
                return result;
            }
            finally
            {
                candidate.Dispose();
            }
        }

        throw new InvalidOperationException("Hyper-V VirtualSystemManagementService was not found.");
    }

    private static ManagementObject? TryGetVideoHead(ManagementObject vm)
    {
        var scope = vm.Scope
            ?? throw new InvalidOperationException("Hyper-V VM object has no management scope.");

        var systemName = (vm["Name"]?.ToString() ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(systemName))
        {
            return null;
        }

        // Query the video head directly by VM system name instead of walking
        // through Msvm_SyntheticDisplayController. Microsoft documents that the
        // synthetic controller is activated only after the guest loads the
        // required acceleration services, while the framebuffer API can be used
        // independently during VM startup.
        var escapedSystemName = EscapeWqlString(systemName);
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery(
                $"SELECT * FROM Msvm_VideoHead WHERE SystemName = '{escapedSystemName}'"));
        using var results = searcher.Get();

        ManagementObject? fallback = null;

        foreach (ManagementObject candidate in results)
        {
            try
            {
                var result = new ManagementObject(
                    scope,
                    new ManagementPath(candidate.Path.Path),
                    null);
                result.Get();

                var width = SafePositiveInt(result["CurrentHorizontalResolution"], 0);
                var height = SafePositiveInt(result["CurrentVerticalResolution"], 0);
                if (width > 0 && height > 0)
                {
                    fallback?.Dispose();
                    return result;
                }

                fallback ??= result;
                if (!ReferenceEquals(fallback, result))
                {
                    result.Dispose();
                }
            }
            finally
            {
                candidate.Dispose();
            }
        }

        return fallback;
    }

    private static string EscapeWqlString(string value) =>
        (value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal);

    private static ManagementObject GetRelatedObject(
        ManagementObject source,
        string resultClass,
        string associationClass,
        string resultRole,
        string sourceRole)
    {
        using var related = source.GetRelated(
            resultClass,
            associationClass,
            null,
            null,
            resultRole,
            sourceRole,
            false,
            null);

        foreach (ManagementObject candidate in related)
        {
            try
            {
                var scope = source.Scope
                    ?? throw new InvalidOperationException("Hyper-V WMI object has no management scope.");
                var result = new ManagementObject(
                    scope,
                    new ManagementPath(candidate.Path.Path),
                    null);
                result.Get();
                return result;
            }
            finally
            {
                candidate.Dispose();
            }
        }

        throw new InvalidOperationException(
            $"Hyper-V did not expose {resultClass} for VM '{source["ElementName"] ?? source["Name"] ?? "unknown"}'.");
    }

    private static void InvokeKeyboardKeyMethod(
        ManagementObject keyboard,
        string methodName,
        uint virtualKeyCode)
    {
        using var input = keyboard.GetMethodParameters(methodName);
        input["keyCode"] = virtualKeyCode;
        using var output = keyboard.InvokeMethod(methodName, input, null);
        EnsureCompleted(output, methodName);
    }

    private static void EnsureCompleted(
        ManagementBaseObject? output,
        string operation,
        ManagementScope? scope = null)
    {
        if (output is null)
        {
            throw new InvalidOperationException($"{operation} returned no WMI result.");
        }

        var returnValue = Convert.ToUInt32(output["ReturnValue"] ?? uint.MaxValue);
        if (returnValue == 0)
        {
            return;
        }

        if (returnValue == 4096 && scope is not null)
        {
            var jobPath = output["Job"]?.ToString();
            if (!string.IsNullOrWhiteSpace(jobPath))
            {
                using var job = new ManagementObject(
                    scope,
                    new ManagementPath(jobPath),
                    null);

                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    job.Get();
                    var jobState = Convert.ToUInt16(job["JobState"] ?? 0);

                    if (jobState == 7)
                    {
                        return;
                    }

                    if (jobState is 8 or 9 or 10)
                    {
                        var description = job["ErrorDescription"]?.ToString();
                        throw new InvalidOperationException(
                            $"{operation} Hyper-V job failed"
                            + (string.IsNullOrWhiteSpace(description) ? "." : $": {description}"));
                    }

                    Thread.Sleep(75);
                }

                throw new TimeoutException($"{operation} Hyper-V WMI job timed out.");
            }
        }

        throw new InvalidOperationException(
            $"{operation} failed with Hyper-V WMI return code {returnValue}.");
    }

    private static ulong ComputeFrameHash(ReadOnlySpan<byte> source)
    {
        // FNV-1a 64-bit: tiny/fast and good enough for detecting unchanged console
        // frames. A hash match only controls reuse of the already-rendered image;
        // it is not used for security or data integrity.
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offset;
        foreach (var value in source)
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }

    private sealed record CachedConsoleImage(
        ulong PixelHash,
        int Width,
        int Height,
        bool FlipVertical,
        string DataUri);

    private static byte[] Rgb565ToPng24Fast(
        byte[] source,
        int width,
        int height,
        bool flipVertical)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // truecolor RGB
        ihdr[10] = 0; // compression method
        ihdr[11] = 0; // filter method
        ihdr[12] = 0; // no interlace
        WritePngChunk(output, "IHDR", ihdr);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[checked(1 + width * 3)];
            row[0] = 0; // PNG filter: None. Fastest for interactive console frames.

            for (var outputY = 0; outputY < height; outputY++)
            {
                var sourceY = flipVertical ? (height - 1 - outputY) : outputY;
                var destination = 1;

                for (var x = 0; x < width; x++)
                {
                    var sourceIndex = checked((sourceY * width + x) * 2);
                    var rgb565 = (ushort)(source[sourceIndex] | (source[sourceIndex + 1] << 8));

                    var r5 = (rgb565 >> 11) & 0x1F;
                    var g6 = (rgb565 >> 5) & 0x3F;
                    var b5 = rgb565 & 0x1F;

                    row[destination++] = (byte)((r5 * 255 + 15) / 31);
                    row[destination++] = (byte)((g6 * 255 + 31) / 63);
                    row[destination++] = (byte)((b5 * 255 + 15) / 31);
                }

                zlib.Write(row, 0, row.Length);
            }
        }

        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        if (typeBytes.Length != 4)
        {
            throw new InvalidOperationException("PNG chunk types must contain exactly four ASCII bytes.");
        }

        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, checked((uint)data.Length));
        output.Write(lengthBytes);
        output.Write(typeBytes);
        if (data.Length > 0)
        {
            output.Write(data);
        }

        var crc = ComputePngCrc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint ComputePngCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        static uint Update(uint current, byte value)
        {
            current ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                current = (current & 1) != 0
                    ? 0xEDB88320u ^ (current >> 1)
                    : current >> 1;
            }
            return current;
        }

        foreach (var value in type)
        {
            crc = Update(crc, value);
        }
        foreach (var value in data)
        {
            crc = Update(crc, value);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static int SafePositiveInt(object? value, int fallback)
    {
        try
        {
            var parsed = Convert.ToInt32(value);
            return parsed > 0 ? parsed : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string NormalizeVmName(string vmName)
    {
        vmName = (vmName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(vmName))
        {
            throw new InvalidOperationException("VM name is required for console access.");
        }

        return vmName;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Hyper-V console access is available only when PenguinPanel runs on Windows.");
        }
    }
}
