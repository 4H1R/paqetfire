using System.ComponentModel;
using PaqetFire.Desktop.ViewModels;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class ApplicationPickerTests
{
    [Theory]
    [InlineData(ApplicationPickerFilter.All, false, false, true)]
    [InlineData(ApplicationPickerFilter.Running, true, false, true)]
    [InlineData(ApplicationPickerFilter.Running, false, true, false)]
    [InlineData(ApplicationPickerFilter.Selected, false, true, true)]
    [InlineData(ApplicationPickerFilter.Selected, true, false, false)]
    public void FilterKeepsMatchingApplications(ApplicationPickerFilter filter, bool running, bool selected, bool expected) =>
        Assert.Equal(expected, filter.Matches(new ApplicationPickerItem { IsRunning = running, IsSelected = selected }));

    [Fact]
    public void SelectionChangeIsReportedSoTheRowCheckBoxFollows()
    {
        var item = new ApplicationPickerItem();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        item.IsSelected = true;
        item.IsSelected = true;

        Assert.Equal([nameof(ApplicationPickerItem.IsSelected)], changed);
    }
}
