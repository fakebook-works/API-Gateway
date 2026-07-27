namespace fakebookGateway.Tests;

using System.Security.Cryptography;

internal static class TestJwtKeys
{
    static TestJwtKeys()
    {
        using var rsa = RSA.Create(2048);
        PrivateKeyBase64 = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
        PublicKeyBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    public const string KeyId = "gateway-test-rs256";
    public static string PrivateKeyBase64 { get; }
    public static string PublicKeyBase64 { get; }

    public static RSA CreatePrivateKey()
    {
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(PrivateKeyBase64), out _);
        return rsa;
    }
}
