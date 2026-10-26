using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using KiteShell.Models;

namespace KiteShell.ViewModels;

public sealed class SessionNodeViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;

    public SessionNodeViewModel(SessionNode node)
    {
        Node = node;
        AccentBrush = new SolidColorBrush(Color.Parse(SessionNode.NormalizeAccent(node.Accent)));
    }

    public SessionNode Node { get; }

    public string Name => Node.Name;

    public bool IsFolder => Node.IsFolder;

    public string Subtitle => Node.IsFolder
        ? (Node.Children.Count == 0 ? "空分组" : $"{Node.Children.Count} 项")
        : Node.Target;

    public IBrush AccentBrush { get; }

    public ObservableCollection<SessionNodeViewModel> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
