using System.Collections.Generic;
using dnSpy.Contracts.Extension;
using MonoWeaver.DnSpyEx.Properties;

namespace MonoWeaver.DnSpyEx;

[ExportExtension]
internal sealed class TheExtension : IExtension
{
    public IEnumerable<string> MergedResourceDictionaries
    {
        get { yield break; }
    }

    public ExtensionInfo ExtensionInfo => new()
    {
        ShortDescription = UiStrings.Get("ExtensionDescription"),
    };

    public void OnEvent(ExtensionEvent @event, object? obj)
    {
    }
}
