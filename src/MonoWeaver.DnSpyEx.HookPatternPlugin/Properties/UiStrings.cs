using System;
using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace MonoWeaver.DnSpyEx.Properties
{
    public static class UiStrings
    {
        public static ResourceManager ResourceManager { get; } = new(
            "MonoWeaver.DnSpyEx.Properties.UiStrings", typeof(UiStrings).Assembly);

        public static string Get(string key)
            => ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

        public static string Format(string key, params object[] arguments)
            => string.Format(CultureInfo.CurrentUICulture, Get(key), arguments);
    }
}

namespace MonoWeaver.DnSpyEx
{
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class LocExtension : MarkupExtension
    {
        public LocExtension(string key) => Key = key;

        [ConstructorArgument("key")]
        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
            => Properties.UiStrings.Get(Key);
    }
}
