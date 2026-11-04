using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class FavoriteCyclePolicyTests
{
    private const string A = @"C:\Apps\A.exe";
    private const string B = @"C:\Apps\B.exe";
    private const string C = @"C:\Apps\C.exe";

    [Theory]
    [InlineData(1, A, B)]
    [InlineData(1, B, C)]
    [InlineData(1, C, A)]
    [InlineData(-1, A, C)]
    [InlineData(-1, B, A)]
    [InlineData(-1, C, B)]
    public void CyclesBothDirectionsAndWrapsInStableExecutableOrder(int direction, string selected, string expected)
    {
        var settings = Favorites(A, B, C);
        settings.SelectMaster(selected, "Master");
        Assert.Equal(Identity(expected), FavoriteCyclePolicy.Next([Live(C), Live(A), Live(B)], settings, direction));
    }

    [Theory]
    [InlineData(1, A)]
    [InlineData(-1, C)]
    public void UnselectedOrUnrelatedMasterStartsAtTheCorrespondingEnd(int direction, string expected)
    {
        var settings = Favorites(A, C);
        Assert.Equal(Identity(expected), FavoriteCyclePolicy.Next([Live(C), Live(A)], settings, direction));
        settings.SelectMaster(@"D:\Other.exe", "Other");
        Assert.Equal(Identity(expected), FavoriteCyclePolicy.Next([Live(C), Live(A)], settings, direction));
    }

    [Fact]
    public void SavedMissingPausedNonfavoriteAndExcludedSourcesCannotBecomeCycleTargets()
    {
        var settings = Favorites(A, B, C);
        settings.FavoriteSources[@"C:\Apps\ShareX.exe"] = "ShareX";
        Assert.Equal(Identity(A), FavoriteCyclePolicy.Next([
            Live(A), Live(B) with { IsActive = false }, Live(@"C:\Apps\Other.exe"),
            Live(@"C:\Apps\ShareX.exe"), Live("system-sounds"), Live("unavailable:1234")
        ], settings, 1));
    }

    [Fact]
    public void EmptyLiveOrFavoriteSetsLeaveTheSelectionAlone()
    {
        Assert.Null(FavoriteCyclePolicy.Next([], Favorites(A), 1));
        Assert.Null(FavoriteCyclePolicy.Next([Live(A)], new SourceSettings(), -1));
        Assert.Null(FavoriteCyclePolicy.Next([Live(A) with { IsActive = false }], Favorites(A), 1));
    }

    [Fact]
    public void OneActiveFavoriteReturnsItselfWithoutChangingSettings()
    {
        var settings = Favorites(A);
        settings.SelectMaster(A, "Master");
        settings.RememberAppliedRate(A, 48000);
        var revision = settings.SelectionRevision;
        Assert.Equal(Identity(A), FavoriteCyclePolicy.Next([Live(A)], settings, 1));
        Assert.Equal(Identity(A), FavoriteCyclePolicy.Next([Live(A)], settings, -1));
        Assert.Equal(A, settings.SelectedSourceKey);
        Assert.Equal(revision, settings.SelectionRevision);
        Assert.Equal(48000, settings.LastKnownSampleRateHz);
        Assert.Single(settings.FavoriteSources);
    }

    [Fact]
    public void NamesPidsSessionOrderAndCaseDoNotChangeTheCycleOrder()
    {
        var settings = Favorites(A, B, C);
        settings.SelectMaster(A.ToLowerInvariant(), "A");
        var sourceB = Live(B) with { ProcessName = "First alphabetically", ProcessIds = [200], SessionToken = "new-B" };
        Assert.Equal(Identity(B), FavoriteCyclePolicy.Next([
            Live(C) with { ProcessName = "AAA" }, sourceB,
            Live(A) with { ProcessIds = [999], SessionToken = "restarted-A" },
            sourceB with { Key = B.ToLowerInvariant(), ProcessIds = [201] }
        ], settings, 1));
        Assert.Equal(Identity(C), FavoriteCyclePolicy.Next([Live(A), Live(C), sourceB], settings, -1));
    }

    [Fact]
    public void SnapshotPathMustMatchItsStableSourceIdentity()
    {
        var settings = Favorites(A);
        Assert.Null(FavoriteCyclePolicy.Next([Live(A) with { ExecutablePath = B }], settings, 1));
        Assert.Null(FavoriteCyclePolicy.Next([Live(A) with { ExecutablePath = "" }], settings, 1));
    }

    [Fact]
    public void CyclingDoesNotChangeMasterPolicyOrPersistAnySnapshotAsASession()
    {
        var settings = Favorites(A, B);
        settings.SelectMaster(A, "A");
        settings.ManualSampleRatesHz[A] = 96000;
        settings.RememberAppliedRate(A, 96000);
        var before = SampleRatePolicy.Evaluate(settings, [Live(A)]);
        var revision = settings.SelectionRevision;
        Assert.Equal(Identity(B), FavoriteCyclePolicy.Next([Live(A), Live(B)], settings, 1));
        Assert.Equal(before, SampleRatePolicy.Evaluate(settings, [Live(A)]));
        Assert.Equal(revision, settings.SelectionRevision);
        Assert.Equal(A, settings.SelectedSourceKey);
        Assert.Equal(96000, settings.LastKnownSampleRateHz);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-2)]
    public void RejectsUnsupportedDirections(int direction) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FavoriteCyclePolicy.Next([], new SourceSettings(), direction));

    private static SourceSettings Favorites(params string[] paths)
    {
        var settings = new SourceSettings();
        foreach (var path in paths) settings.SetFavorite(path, path, true);
        return settings;
    }

    private static string Identity(string path) => SourceIdentity.FromExecutablePath(path);
    private static SourceSnapshot Live(string path) => new(path, "Player", path, [123], true, "session");
}
