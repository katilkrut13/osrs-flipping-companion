using System.Security.Cryptography;
using System.Text;
using CommonStorage = OsrsFlipping.Core.Storage;

namespace OsrsFlipping.Core;

public static class Credentials
{
    public static void StoreBankPin(string path, string account, string pin)
    {
        if (pin.Length != 4 || !pin.All(c => c is >= '0' and <= '9')) throw new ArgumentException("Four digit PIN required");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows encrypted storage required");
        var clear = Encoding.UTF8.GetBytes(pin);
        try
        {
            var encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            CommonStorage.WriteJson(path, J.O(("account_key", account), ("protected", Convert.ToBase64String(encrypted))));
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public static string? LoadBankPin(string path, string account)
    {
        if (!File.Exists(path)) return null;
        var record = CommonStorage.ReadJson(path) as System.Text.Json.Nodes.JsonObject ?? throw new InvalidDataException("Invalid encrypted credential record");
        if (record.S("account_key") != account) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows encrypted storage required");
        var clear = ProtectedData.Unprotect(Convert.FromBase64String(record.S("protected")), null, DataProtectionScope.CurrentUser);
        try
        {
            var pin = Encoding.UTF8.GetString(clear);
            if (pin.Length != 4 || !pin.All(c => c is >= '0' and <= '9')) throw new InvalidDataException("Invalid protected bank credential");
            return pin;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
