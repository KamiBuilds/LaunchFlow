namespace LaunchFlow.Services;
public static class WebsiteLoginMode
{
    public const string Auto = "Auto";
    public const string None = "None";
    public const string Sso = "SSO";
    public static int Index(string? mode) => mode switch { None => 1, Sso => 2, _ => 0 };
    public static string FromIndex(int index) => index switch { 1 => None, 2 => Sso, _ => Auto };
    public static bool UsesSavedPassword(string? mode) =>
        string.Equals(Normalize(mode), Auto, StringComparison.Ordinal);
    public static string Normalize(string? mode) => mode switch { None => None, Sso => Sso, _ => Auto };
    public static string Resolve(string? loginMode, bool autoLogin)
    {
        if (!string.IsNullOrWhiteSpace(loginMode))
        {
            if (string.Equals(loginMode, None, StringComparison.OrdinalIgnoreCase)) return None;
            if (string.Equals(loginMode, Sso, StringComparison.OrdinalIgnoreCase)) return Sso;
            if (string.Equals(loginMode, Auto, StringComparison.OrdinalIgnoreCase)) return Auto;
        }
        return autoLogin ? Auto : None;
    }
}
