using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TurntableSpeed.App.ViewModels;

/// <summary>
/// Change notification, hand-rolled. A whole MVVM package would be one more dependency to
/// restore for a base class of this size.
/// </summary>
public abstract class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
