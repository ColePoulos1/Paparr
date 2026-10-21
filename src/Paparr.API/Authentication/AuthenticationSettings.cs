namespace Paparr.API.Authentication;

public sealed class AuthenticationSettings
{
    public bool Enabled { get; set; } = true;
    public string PasswordHash { get; set; } = "";
    public bool SecureCookies { get; set; }
    public string KeysPath { get; set; } = ".paparr/keys";
}
