using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingControl.Core.Models;

namespace RingControl.App.ViewModels;

public sealed partial class EventItemViewModel : ObservableObject
{
    private readonly DeviceViewModel _owner;

    public EventItemViewModel(DeviceViewModel owner, RingEvent ringEvent)
    {
        _owner = owner;
        Event = ringEvent;
    }

    public RingEvent Event { get; }

    public string Id => Event.Id;

    public string KindLabel => Event.KindLabel;

    public string TimeLabel => Event.TimeLabel;

    public string DurationLabel => Event.DurationLabel;

    public string Glyph => Event.Glyph;

    public bool PersonDetected => Event.PersonDetected;

    public string? ThumbnailUrl => Event.ThumbnailUrl;

    public bool HasClip => !string.IsNullOrWhiteSpace(Event.Id);

    [ObservableProperty]
    private ImageSource? thumbnail;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? status;

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    public void SetThumbnail(ImageSource? image) => Thumbnail = image;

    [RelayCommand]
    private Task PlayAsync() => _owner.PlayEventAsync(this);

    [RelayCommand]
    private Task SaveAsync() => _owner.SaveEventAsync(this);
}
