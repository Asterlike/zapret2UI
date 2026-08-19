using System.Windows.Markup;

namespace Zapret2UI.Localization;

/// <summary>
/// XAML sugar for <see cref="Loc.T(string)"/>: write <c>Text="{loc:Loc 'Русский текст'}"</c> and the
/// bound value becomes the English translation (or the Russian source, untranslated). The Russian text is
/// both the displayed default and the lookup key, so the XAML stays readable in Russian.
///
/// It resolves once, at parse time. That is correct here because the window is parsed only after
/// <see cref="Loc.Init"/> runs in <c>App.OnStartup</c>, and changing language restarts the app — so a
/// parse-time value is never stale. Wrap a value containing a comma or an <c>=</c> in single quotes
/// (<c>{loc:Loc 'вкл, выкл'}</c>); a leading <c>{</c> needs the usual <c>{}</c> escape.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    /// <summary>The Russian source string, which is also the lookup key.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
