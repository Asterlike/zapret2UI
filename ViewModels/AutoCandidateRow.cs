using Zapret2UI.Mvvm;
using Zapret2UI.Services;

namespace Zapret2UI.ViewModels;

public enum AutoCandidateState { Pending, Running, Done }

/// <summary>
/// One row in the auto-select popup: a candidate strategy that moves through
/// Pending → Running → Done, carrying its <see cref="AutoScore"/> once probed.
/// </summary>
public sealed class AutoCandidateRow : ObservableObject
{
    public string Name { get; }

    public AutoCandidateRow(string name) => Name = name;

    private AutoCandidateState _state = AutoCandidateState.Pending;
    public AutoCandidateState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(Glyph));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    private AutoScore? _score;
    public AutoScore? Score
    {
        get => _score;
        private set
        {
            if (SetField(ref _score, value))
            {
                OnPropertyChanged(nameof(Glyph));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(HasHosts));
            }
        }
    }

    /// <summary>Attach the probe result and mark the row done.</summary>
    public void Apply(AutoScore s) { Score = s; State = AutoCandidateState.Done; }

    public bool IsPending => State == AutoCandidateState.Pending;
    public bool IsRunning => State == AutoCandidateState.Running;
    public bool IsDone => State == AutoCandidateState.Done;

    /// <summary>✓ / ≈ / ✗ when done, • while waiting/probing.</summary>
    public string Glyph => IsDone ? (Score?.Glyph ?? "•") : "•";

    public string StatusText => State switch
    {
        AutoCandidateState.Running => "проверяю…",
        AutoCandidateState.Done => Score?.Detail ?? "готово",
        _ => "в очереди",
    };

    public bool HasHosts => Score?.HostList.Count > 0;
    public IReadOnlyList<AutoHostResult> HostList => Score?.HostList ?? Array.Empty<AutoHostResult>();
}
