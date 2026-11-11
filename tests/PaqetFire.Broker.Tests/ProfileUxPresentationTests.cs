using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.ViewModels;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class ProfileUxPresentationTests
{
    private static readonly Guid Office = Guid.NewGuid();
    private static readonly Guid Travel = Guid.NewGuid();
    private static readonly Guid Backup = Guid.NewGuid();

    private static List<ProfileDisplayItem> Catalog(Guid active, Guid @default) =>
    [
        new() { Id = Office, Name = "Office", IsActive = active == Office, IsDefault = @default == Office },
        new() { Id = Travel, Name = "Travel", IsActive = active == Travel, IsDefault = @default == Travel },
        new() { Id = Backup, Name = "Backup", IsActive = active == Backup, IsDefault = @default == Backup },
    ];

    [Theory]
    [InlineData(BrokerConnectionState.Connected, true)]
    [InlineData(BrokerConnectionState.Connecting, true)]
    [InlineData(BrokerConnectionState.Degraded, true)]
    [InlineData(BrokerConnectionState.Guarded, true)]
    [InlineData(BrokerConnectionState.Disconnected, false)]
    [InlineData(BrokerConnectionState.NotReady, false)]
    public void OnlyALiveRouteRestartsOnAProfileChange(BrokerConnectionState state, bool restarts) =>
        Assert.Equal(restarts, ProfileSwitch.RestartsRoute(state));

    [Fact]
    public void SwitchWordingSaysWhatHappensToTheRouteAndTheEdits()
    {
        Assert.Equal("Switch to 'Travel'?", ProfileSwitch.SwitchTitle("Travel"));
        Assert.Equal("The route reconnects with the new profile.", ProfileSwitch.SwitchMessage(true, false));
        Assert.Contains("discarded", ProfileSwitch.SwitchMessage(true, true), StringComparison.Ordinal);
        Assert.Contains("reconnects", ProfileSwitch.SwitchMessage(true, true), StringComparison.Ordinal);
        Assert.DoesNotContain("reconnects", ProfileSwitch.SwitchMessage(false, true), StringComparison.Ordinal);
        Assert.Equal("Switch and reconnect", ProfileSwitch.SwitchAction(true, false));
        Assert.Equal("Discard and switch", ProfileSwitch.SwitchAction(true, true));
    }

    [Fact]
    public void DuplicateWordingMentionsTheReconnectOnlyWhenTheRouteIsLive()
    {
        Assert.Contains("reconnects", ProfileSwitch.DuplicateMessage(true, false), StringComparison.Ordinal);
        Assert.DoesNotContain("reconnects", ProfileSwitch.DuplicateMessage(false, true), StringComparison.Ordinal);
        Assert.Contains("discarded", ProfileSwitch.DuplicateMessage(false, true), StringComparison.Ordinal);
        Assert.Equal("Duplicate and reconnect", ProfileSwitch.DuplicateAction(true, false));
    }

    [Fact]
    public void DeletingTheActiveProfileActivatesTheDefault()
    {
        var next = ProfileSwitch.ActiveAfterDelete(Catalog(active: Office, @default: Backup), Office);
        Assert.Equal(Backup, next?.Id);
    }

    [Fact]
    public void DeletingTheActiveDefaultProfileActivatesTheFirstRemaining()
    {
        var next = ProfileSwitch.ActiveAfterDelete(Catalog(active: Office, @default: Office), Office);
        Assert.Equal(Travel, next?.Id);
    }

    [Fact]
    public void DeletingAnotherProfileKeepsTheActiveOne()
    {
        var next = ProfileSwitch.ActiveAfterDelete(Catalog(active: Office, @default: Travel), Travel);
        Assert.Equal(Office, next?.Id);
    }

    [Fact]
    public void DeletingTheLastProfileWhileConnectedIsNotOffered()
    {
        var only = new List<ProfileDisplayItem> { new() { Id = Office, Name = "Office", IsActive = true, IsDefault = true } };
        var next = ProfileSwitch.ActiveAfterDelete(only, Office);
        Assert.Null(next);
        Assert.False(ProfileSwitch.CanDelete(deletesActive: true, restartsRoute: true, next));
        Assert.True(ProfileSwitch.CanDelete(deletesActive: true, restartsRoute: false, next));
        Assert.Contains("Disconnect the route first", ProfileSwitch.DeleteMessage(true, true, next), StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteWordingNamesTheProfileTheRouteReconnectsWith()
    {
        var next = new ProfileDisplayItem { Id = Backup, Name = "Backup" };
        Assert.Contains("reconnects with 'Backup'", ProfileSwitch.DeleteMessage(true, true, next), StringComparison.Ordinal);
        Assert.DoesNotContain("reconnects", ProfileSwitch.DeleteMessage(false, true, next), StringComparison.Ordinal);
        Assert.DoesNotContain("reconnects", ProfileSwitch.DeleteMessage(true, false, next), StringComparison.Ordinal);
    }

    [Fact]
    public void ImportListsShortFilesByNameAndCountsLongOnes()
    {
        Assert.Equal("The file contains 1 profile: Office.", ProfileImportText.Contents(["Office"]));
        Assert.Equal("The file contains 2 profiles: Office, Travel.", ProfileImportText.Contents(["Office", "Travel"]));
        var many = Enumerable.Range(1, ProfileImportText.NamedProfileLimit + 1).Select(index => $"Server {index}").ToList();
        Assert.Equal($"The file contains {many.Count} profiles.", ProfileImportText.Contents(many));
    }

    [Fact]
    public void ImportExplainsWhyReplacingIsUnavailableWhileConnected()
    {
        Assert.Contains("only while the route is disconnected", ProfileImportText.ChoiceMessage(["Office"], canReplace: false), StringComparison.Ordinal);
        Assert.Contains("or replace every saved profile", ProfileImportText.ChoiceMessage(["Office"], canReplace: true), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacingIsConfirmedWithTheNumberOfProfilesItDeletes()
    {
        Assert.Equal("Replace all 4 existing profiles?", ProfileImportText.ReplaceTitle(4));
        Assert.Contains("the 2 profiles in the file", ProfileImportText.ReplaceMessage(2, hasPendingChanges: false), StringComparison.Ordinal);
        Assert.Contains("unsaved changes", ProfileImportText.ReplaceMessage(1, hasPendingChanges: true), StringComparison.Ordinal);
        Assert.DoesNotContain("unsaved changes", ProfileImportText.ReplaceMessage(1, hasPendingChanges: false), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("null")]
    [InlineData("NULL")]
    public void BothNoEncryptionSpellingsShowAsOneInsecureChoice(string saved)
    {
        Assert.True(AdvancedTransportText.IsUnencrypted(saved));
        Assert.Equal(AdvancedTransportText.NoEncryption, AdvancedTransportText.EncryptionChoice(saved));
        Assert.Equal("none (insecure)", AdvancedTransportText.EncryptionLabel(AdvancedTransportText.EncryptionChoice(saved)));
    }

    [Fact]
    public void ThePickerOffersNoEncryptionOnceAndKeepsOtherAlgorithms()
    {
        Assert.Single(AdvancedTransportText.EncryptionChoices, AdvancedTransportText.IsUnencrypted);
        Assert.Equal("aes", AdvancedTransportText.EncryptionChoice("aes"));
        Assert.Equal("aes", AdvancedTransportText.EncryptionLabel("aes"));
        Assert.False(AdvancedTransportText.IsUnencrypted("aes"));
        Assert.All(AdvancedTransportText.EncryptionChoices, choice =>
            Assert.Empty(PaqetFire.Core.Configuration.PaqetConfigurationValidator.ValidateAdvanced(
                new PaqetFire.Core.Configuration.PaqetAdvancedOptions { KcpBlock = choice }, "fast")));
    }

    [Theory]
    [InlineData(1.5, 2.0)]
    [InlineData(2.4, 2.0)]
    [InlineData(-0.5, -1.0)]
    public void FractionsRoundToTheStoredWholeNumber(double typed, double stored) =>
        Assert.Equal(stored, AdvancedTransportText.WholeNumberFor(typed));

    [Theory]
    [InlineData(3.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void WholeAndBlankNumbersNeedNoRounding(double typed) =>
        Assert.Null(AdvancedTransportText.WholeNumberFor(typed));

    [Fact]
    public void ProfilesWithoutATransportKeyAreMarked()
    {
        Assert.Equal("Office", ProfileDisplayName.Format("Office", isDefault: false, hasTransportKey: true));
        Assert.Equal("Office · default", ProfileDisplayName.Format("Office", isDefault: true, hasTransportKey: true));
        Assert.Equal("Travel · needs key", ProfileDisplayName.Format("Travel", isDefault: false, hasTransportKey: false));
        Assert.Equal("Travel · default · needs key", ProfileDisplayName.Format("Travel", isDefault: true, hasTransportKey: false));
    }
}
