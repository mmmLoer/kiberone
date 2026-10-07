using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace Kiberone.Infrastructure;

/// <summary>Credentials are scoped to the current OS user, server and classroom location.</summary>
public sealed class LocationCredentialStore(string? directory = null)
{
    private readonly object gate = new();
    private const string Service = "KIBERone Tutor location";
    private readonly string root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone", "Tutor", "credentials");
    public static string Scope(string server, string location)
    {
        var uri = new Uri(server, UriKind.Absolute);
        if (uri.UserInfo.Length != 0 || string.IsNullOrWhiteSpace(location)) throw new ArgumentException("Укажите сервер и локацию.");
        var value = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "\n" + location.Trim().Normalize().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
    public string? Read(string server, string location) { lock (gate) return ReadCore(server, location); }
    private string? ReadCore(string server, string location)
    {
        var scope = Scope(server, location);
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(root, scope + ".dpapi");
            if (!File.Exists(path)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Encoding.UTF8.GetBytes(scope), DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        if (OperatingSystem.IsMacOS()) return MacRead(scope);
        return null;
    }
    public void Save(string server, string location, string password) { lock (gate) SaveCore(server, location, password); }
    private void SaveCore(string server, string location, string password)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Введите пароль локации.");
        var scope = Scope(server, location);
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, scope + ".dpapi");
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temporary, ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes(scope), DataProtectionScope.CurrentUser));
                    File.Move(temporary, path, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            else if (OperatingSystem.IsMacOS()) MacSave(scope, bytes);
            else throw new PlatformNotSupportedException("Сохранение пароля доступно на Windows и macOS.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static string? MacRead(string scope)
    {
        var service = Encoding.UTF8.GetBytes(Service); var account = Encoding.UTF8.GetBytes(scope);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, out var length, out var data, out var item);
        try
        {
            if (status == -25300) return null;
            Check(status);
            var bytes = new byte[checked((int)length)];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data); if (item != IntPtr.Zero) CFRelease(item); }
    }
    private static void MacSave(string scope, byte[] password)
    {
        var service = Encoding.UTF8.GetBytes(Service); var account = Encoding.UTF8.GetBytes(scope);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, out _, out var data, out var item);
        if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
        try
        {
            if (status == 0) Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password));
            else if (status == -25300) Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, (uint)password.Length, password, out item));
            else Check(status);
        }
        finally { if (item != IntPtr.Zero) CFRelease(item); }
    }
    private static void Check(int status) { if (status != 0) throw new InvalidOperationException("Не удалось сохранить или прочитать пароль в связке ключей."); }
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [DllImport(Security)] private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint passwordLength, out IntPtr password, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint passwordLength, byte[] password, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr item);
}
