using System.ComponentModel;

namespace PaqetFire.Desktop.ViewModels;

public sealed class ApplicationPickerItem : INotifyPropertyChanged
{
    private bool isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayName { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public string StateText { get; set; } = string.Empty;

    public bool IsRunning { get; set; }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

/// <summary>Which discovered applications the picker lists.</summary>
public enum ApplicationPickerFilter
{
    All,
    Running,
    Selected,
}

public static class ApplicationPickerFilterExtensions
{
    public static bool Matches(this ApplicationPickerFilter filter, ApplicationPickerItem item) => filter switch
    {
        ApplicationPickerFilter.Running => item.IsRunning,
        ApplicationPickerFilter.Selected => item.IsSelected,
        _ => true,
    };
}
