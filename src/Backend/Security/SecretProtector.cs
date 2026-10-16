using Microsoft.AspNetCore.DataProtection;

internal sealed class SecretProtector
{
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("CS2AccountHub.SteamAuthenticatorSharedSecret.v1");
    }

    public string Protect(string secret) => _protector.Protect(secret);
    public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);
}
