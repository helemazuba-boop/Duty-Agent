using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace DutyAgent.Services;

public static class SecurityHelper
{
    private const string AesMacPrefix = "aesmac:v1:";
    private const int AesKeyBytes = 32;
    private const int HmacKeyBytes = 32;
    private const int SaltBytes = 16;
    private const int IvBytes = 16;
    private const int HmacBytes = 32;
    private const int KeyDerivationIterations = 120_000;

    private static readonly byte[] AppBindingEntropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("Duty-Agent.ApiKey.MacBinding.v1"));

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
            "npcap"
        };
        return ignoredKeywords.Any(adapterText.Contains);
    }
}
