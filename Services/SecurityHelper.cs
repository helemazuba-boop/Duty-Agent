using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DutyAgent.Services;

public static class SecurityHelper
{
    private const string AesMacPrefix = "aesmac:v1:";
    private const string Pbkdf2Sha256Prefix = "pbkdf2_sha256";
    // 契约 C2/D4：plan_presets[].api_key 的 DPAPI 密文前缀，Python 侧
    // dpapi_compat.unprotect() 按该前缀识别并解密（失败置空 + warn，不抛）。
    private const string DpapiPrefix = "dpapi:v1:";
    private const uint CryptProtectUiForbidden = 0x1;
    private const int AesKeyBytes = 32;
    private const int HmacKeyBytes = 32;
    private const int SaltBytes = 16;
    private const int IvBytes = 16;
    private const int HmacBytes = 32;
    private const int VerifierKeyBytes = 32;
    private const int KeyDerivationIterations = 120_000;

    private static readonly byte[] AppBindingEntropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("Duty-Agent.ApiKey.MacBinding.v1"));

    #region DPAPI (当前用户作用域)

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    public static bool IsDpapiProtected(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.StartsWith(DpapiPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// 以当前 Windows 用户作用域加密（CryptProtectData），
    /// 返回 "dpapi:v1:&lt;base64>"，仅本机本用户可解。
    /// 加密失败抛 CryptographicException —— 调用方决定是否明文降级。
    /// </summary>
    public static string ProtectForCurrentUser(string plainText)
    {
        ArgumentException.ThrowIfNullOrEmpty(plainText);

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var input = CreateBlob(plainBytes);
        try
        {
            if (!CryptProtectData(ref input, "Duty-Agent plan api_key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                throw new CryptographicException($"CryptProtectData failed (win32 error {Marshal.GetLastWin32Error()}).");
            }

            var cipherBytes = ReadBlobAndFreeBuffer(output);
            return DpapiPrefix + Convert.ToBase64String(cipherBytes);
        }
        finally
        {
            Marshal.FreeHGlobal(input.PbData);
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <summary>
    /// 解密 "dpapi:v1:&lt;base64>" 密文；格式不符/解密失败返回 null（不抛，
    /// 与 Python 侧 dpapi_compat 的容错语义对齐）。
    /// </summary>
    public static string? UnprotectCurrentUser(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText) || !IsDpapiProtected(cipherText))
        {
            return null;
        }

        byte[] cipherBytes;
        try
        {
            cipherBytes = Convert.FromBase64String(cipherText[DpapiPrefix.Length..]);
        }
        catch (FormatException)
        {
            return null;
        }

        var input = CreateBlob(cipherBytes);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                return null;
            }

            return Encoding.UTF8.GetString(ReadBlobAndFreeBuffer(output));
        }
        finally
        {
            Marshal.FreeHGlobal(input.PbData);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { CbData = bytes.Length, PbData = pointer };
    }

    /// <summary>把 CRYPTPROTECT 分配的输出缓冲区拷贝为托管数组并释放原缓冲区。</summary>
    private static byte[] ReadBlobAndFreeBuffer(DataBlob blob)
    {
        try
        {
            var bytes = new byte[blob.CbData];
            if (blob.CbData > 0)
            {
                Marshal.Copy(blob.PbData, bytes, 0, blob.CbData);
            }

            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(blob.PbData);
        }
    }

    #endregion

    public static bool IsCurrentEncryptionFormat(string? encryptedText)
    {
        return !string.IsNullOrWhiteSpace(encryptedText) &&
               encryptedText.StartsWith(AesMacPrefix, StringComparison.Ordinal);
    }

    public static string EncryptString(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var macAddress = GetBestMacAddress(); // Use the most stable MAC for new encryption
        var (aesKey, hmacKey) = DeriveKeysForCurrentMachine(salt, macAddress);

        try
        {
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = aesKey;
            aes.GenerateIV();

            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            var payload = new byte[SaltBytes + IvBytes + cipherBytes.Length];
            Buffer.BlockCopy(salt, 0, payload, 0, SaltBytes);
            Buffer.BlockCopy(aes.IV, 0, payload, SaltBytes, IvBytes);
            Buffer.BlockCopy(cipherBytes, 0, payload, SaltBytes + IvBytes, cipherBytes.Length);

            var mac = HMACSHA256.HashData(hmacKey, payload);
            var combined = new byte[payload.Length + mac.Length];
            Buffer.BlockCopy(payload, 0, combined, 0, payload.Length);
            Buffer.BlockCopy(mac, 0, combined, payload.Length, mac.Length);

            return AesMacPrefix + Convert.ToBase64String(combined);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
            CryptographicOperations.ZeroMemory(hmacKey);
        }
    }

    public static string DecryptString(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText))
        {
            return string.Empty;
        }

        if (!IsCurrentEncryptionFormat(encryptedText))
        {
            throw new CryptographicException("Unsupported API key format. Re-enter the key on this device.");
        }

        return DecryptAesMacString(encryptedText);
    }

    public static string CreatePasswordVerifier(string plainText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainText);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var derivedKey = DeriveVerifierKey(plainText, salt);
        try
        {
            return string.Create(
                Pbkdf2Sha256Prefix.Length + 1 + "120000".Length + 1 + Convert.ToBase64String(salt).Length + 1 + Convert.ToBase64String(derivedKey).Length,
                (Salt: salt, Hash: derivedKey),
                static (span, state) =>
                {
                    var verifier = $"{Pbkdf2Sha256Prefix}${KeyDerivationIterations}${Convert.ToBase64String(state.Salt)}${Convert.ToBase64String(state.Hash)}";
                    verifier.AsSpan().CopyTo(span);
                });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    public static bool VerifyPasswordVerifier(string plainText, string verifier)
    {
        if (string.IsNullOrWhiteSpace(plainText) || string.IsNullOrWhiteSpace(verifier))
        {
            return false;
        }

        var parts = verifier.Split('$');
        if (parts.Length != 4 ||
            !string.Equals(parts[0], Pbkdf2Sha256Prefix, StringComparison.Ordinal) ||
            !int.TryParse(parts[1], out var iterations) ||
            iterations != KeyDerivationIterations)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expectedHash = Convert.FromBase64String(parts[3]);
            if (salt.Length != SaltBytes || expectedHash.Length != VerifierKeyBytes)
            {
                return false;
            }

            var actualHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(plainText),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                VerifierKeyBytes);
            try
            {
                return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualHash);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string DecryptAesMacString(string encryptedText)
    {
        var payloadBase64 = encryptedText[AesMacPrefix.Length..];
        var allBytes = Convert.FromBase64String(payloadBase64);
        var minSize = SaltBytes + IvBytes + HmacBytes + 1;
        if (allBytes.Length < minSize)
        {
            throw new FormatException("Invalid encrypted payload.");
        }

        var macOffset = allBytes.Length - HmacBytes;
        var payloadBytes = new byte[macOffset];
        var expectedMac = new byte[HmacBytes];
        Buffer.BlockCopy(allBytes, 0, payloadBytes, 0, payloadBytes.Length);
        Buffer.BlockCopy(allBytes, macOffset, expectedMac, 0, expectedMac.Length);

        var salt = new byte[SaltBytes];
        Buffer.BlockCopy(payloadBytes, 0, salt, 0, salt.Length);
        var cipherBytesLength = payloadBytes.Length - SaltBytes - IvBytes;
        if (cipherBytesLength <= 0 || cipherBytesLength % IvBytes != 0)
        {
            throw new FormatException("Invalid encrypted payload.");
        }

        // 核心变更：遍历所有物理 MAC 尝试解密
        var candidates = GetCandidateMacAddresses();
        Exception? lastEx = null;

        foreach (var macAddress in candidates)
        {
            try
            {
                var (aesKey, hmacKey) = DeriveKeysForCurrentMachine(salt, macAddress);
                try
                {
                    var actualMac = HMACSHA256.HashData(hmacKey, payloadBytes);
                    if (!CryptographicOperations.FixedTimeEquals(actualMac, expectedMac))
                    {
                        continue; // HMAC mismatch, try next MAC
                    }

                    // HMAC verified, decrypt now
                    var iv = new byte[IvBytes];
                    var cipherBytes = new byte[cipherBytesLength];
                    Buffer.BlockCopy(payloadBytes, SaltBytes, iv, 0, iv.Length);
                    Buffer.BlockCopy(payloadBytes, SaltBytes + IvBytes, cipherBytes, 0, cipherBytes.Length);

                    using var aes = Aes.Create();
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = aesKey;
                    aes.IV = iv;

                    using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                    var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                    return Encoding.UTF8.GetString(plainBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(aesKey);
                    CryptographicOperations.ZeroMemory(hmacKey);
                }
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }

        throw new CryptographicException("API key decryption failed. No matching network adapter found.", lastEx);
    }

    private static byte[] DeriveVerifierKey(string plainText, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(plainText),
            salt,
            KeyDerivationIterations,
            HashAlgorithmName.SHA256,
            VerifierKeyBytes);
    }

    private static (byte[] AesKey, byte[] HmacKey) DeriveKeysForCurrentMachine(byte[] salt, string macAddress)
    {
        var bindingMaterial = SHA256.HashData(Encoding.UTF8.GetBytes($"mac:{macAddress}"));
        var seed = new byte[bindingMaterial.Length + AppBindingEntropy.Length];
        Buffer.BlockCopy(bindingMaterial, 0, seed, 0, bindingMaterial.Length);
        Buffer.BlockCopy(AppBindingEntropy, 0, seed, bindingMaterial.Length, AppBindingEntropy.Length);
        var password = Convert.ToBase64String(SHA256.HashData(seed));

        using var kdf = new Rfc2898DeriveBytes(password, salt, KeyDerivationIterations, HashAlgorithmName.SHA256);
        var keyMaterial = kdf.GetBytes(AesKeyBytes + HmacKeyBytes);
        var aesKey = new byte[AesKeyBytes];
        var hmacKey = new byte[HmacKeyBytes];
        Buffer.BlockCopy(keyMaterial, 0, aesKey, 0, AesKeyBytes);
        Buffer.BlockCopy(keyMaterial, AesKeyBytes, hmacKey, 0, HmacKeyBytes);

        CryptographicOperations.ZeroMemory(seed);
        CryptographicOperations.ZeroMemory(keyMaterial);
        CryptographicOperations.ZeroMemory(bindingMaterial);
        return (aesKey, hmacKey);
    }

    // 获取用于加密的最佳 MAC 地址（优先选择最稳定的，如 Ethernet）
    private static string GetBestMacAddress()
    {
        var candidates = GetCandidateMacAddresses();
        if (candidates.Count == 0)
        {
             throw new InvalidOperationException("No usable physical MAC address found for API key encryption.");
        }
        return candidates[0];
    }

    // 获取所有候选物理 MAC 地址
    private static List<string> GetCandidateMacAddresses()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => !IsIgnoredAdapter(nic))
            .Select(nic => new
            {
                Mac = NormalizeMac(nic.GetPhysicalAddress()),
                IsUp = nic.OperationalStatus == OperationalStatus.Up,
                Type = nic.NetworkInterfaceType
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Mac))
            .ToList();

        // 排序逻辑：
        // 1. Ethernet 优先 (通常是板载有线网卡，最稳定)
        // 2. Wireless 其次 (通常是板载无线网卡)
        // 3. Up 状态优先
        // 4. 字母序
        return adapters
            .OrderByDescending(x => x.Type == NetworkInterfaceType.Ethernet)
            .ThenByDescending(x => x.Type == NetworkInterfaceType.Wireless80211)
            .ThenByDescending(x => x.IsUp)
            .ThenBy(x => x.Mac, StringComparer.Ordinal)
            .Select(x => x.Mac)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeMac(PhysicalAddress? mac)
    {
        var raw = mac?.ToString()?.Trim() ?? string.Empty;
        if (raw.Length != 12 || raw == "000000000000")
        {
            return string.Empty;
        }

        return raw.ToUpperInvariant();
    }

    private static bool IsIgnoredAdapter(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
            nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
        {
            return true;
        }

        var adapterText = $"{nic.Name} {nic.Description}".ToLowerInvariant();
        var ignoredKeywords = new[]
        {
            "virtual",
            "vmware",
            "hyper-v",
            "vbox",
            "loopback",
            "bluetooth",
            "vpn",
            "wireguard",
            "tap",
            "npcap",
            "vethernet",
            "vmnet",
            "virtual switch",
            "ndis",
            "hyperv",
            "wsl",
        };
        return ignoredKeywords.Any(adapterText.Contains);
    }
}
