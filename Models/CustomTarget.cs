using Zapret2UI.Mvvm;

namespace Zapret2UI.Models;

/// <summary>
/// A user-defined bypass target: a root domain plus the list of domains discovered
/// for it (via crt.sh) or entered by hand. Stored as a <c>target-&lt;name&gt;.txt</c>
/// hostlist; its domains feed diagnostics, auto-select scoring, generation, and the
/// engine's effective bypass scope (see <see cref="Services.TargetService"/>).
/// </summary>
public sealed class CustomTarget : ObservableObject
{
    public required string Name { get; init; }   // root domain — also the file slug

    private int _domainCount;
    public int DomainCount
    {
        get => _domainCount;
        set { if (SetField(ref _domainCount, value)) OnPropertyChanged(nameof(Subtitle)); }
    }

    public string Subtitle => $"{DomainCount} домен(ов)";
}
