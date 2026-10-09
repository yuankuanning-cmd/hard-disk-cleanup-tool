using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ChatGPTCleanupTool.Models;

public sealed class CleanupCategoryItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Risk { get; init; }
    public required bool DefaultSelected { get; init; }
    public required int FileCount { get; init; }
    public required long Bytes { get; init; }
    public required string DisplaySize { get; init; }
    public required IReadOnlyList<CleanupFileItem> Files { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class CleanupFileItem : INotifyPropertyChanged
{
    private bool _isExcluded;

    public required string CategoryId { get; init; }
    public required string Root { get; init; }
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public required long Bytes { get; init; }
    public required string DisplaySize { get; init; }
    public required DateTime LastWriteTime { get; init; }

    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (_isExcluded == value)
            {
                return;
            }

            _isExcluded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExcluded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
