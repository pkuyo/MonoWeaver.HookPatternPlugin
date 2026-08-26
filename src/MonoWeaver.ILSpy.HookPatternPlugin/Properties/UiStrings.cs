using System.Globalization;
using System.Resources;

namespace MonoWeaver.ILSpy.Properties;

internal static class UiStrings
{
    private static readonly ResourceManager Resources = new(
        "MonoWeaver.ILSpy.Properties.UiStrings", typeof(UiStrings).Assembly);

    public static string Get(string key)
        => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object[] arguments)
        => string.Format(CultureInfo.CurrentUICulture, Get(key), arguments);
}
