using Microsoft.AspNetCore.DataProtection;

internal sealed class TokenProtector
{
    private readonly IDataProtector _protector;

    public TokenProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("CS2AccountHub.SteamRefreshToken.v1");
    }

    public string Protect(string token) => _protector.Protect(token);
    public string Unprotect(string token) => _protector.Unprotect(token);
}
