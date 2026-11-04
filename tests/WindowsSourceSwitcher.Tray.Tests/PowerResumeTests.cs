using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using WindowsLosslessSwitcher.Models;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class PowerResumeTests
{
    [Fact]
    public void NativePowerMessagesPreservePreferencesBlockSuspendedRefreshAndRecoverRepeatedly() => OnSta(() =>
    {
        using var host = new TrayContext(diagnosticsMode: true);
        var popup = Field<SourcePopup>(host, "_popup");
        var settings = Field<SourceSettings>(host, "_settings");
        settings.TargetEndpointId = "retained-output";
        settings.SelectMaster(@"C:\PLAYER.EXE", "Player");
        settings.RememberAppliedRate(settings.SelectedSourceKey!, 96000);
        settings.ManualSampleRatesHz[settings.SelectedSourceKey!] = 96000;
        var before = JsonSerializer.Serialize(settings);
        try
        {
            PumpUntil(() => !Field<bool>(host, "_refreshing"));
            for (var index = 0; index < 4; index++)
            {
                Assert.Equal((nint)1, SendMessage(popup.Handle, 0x218, 4, 0));
                Assert.True(Field<bool>(host, "_suspended"));
                Assert.False(Field<System.Windows.Forms.Timer>(host, "_timer").Enabled);
                var epoch = Field<long>(host, "_epoch");
                SendMessage(popup.Handle, 0x218, 4, 0); // Duplicate suspend is idempotent.
                Assert.Equal(epoch, Field<long>(host, "_epoch"));
                typeof(TrayContext).GetMethod("StartRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [true]);
                Assert.False(Field<System.Windows.Forms.Timer>(host, "_notificationTimer").Enabled);
                SendMessage(popup.Handle, 0x218, 18, 0);
                Assert.False(Field<bool>(host, "_suspended"));
                Assert.True(Field<System.Windows.Forms.Timer>(host, "_timer").Enabled);
                epoch = Field<long>(host, "_epoch");
                SendMessage(popup.Handle, 0x218, 7, 0); // User resume follows automatic resume.
                Assert.Equal(epoch, Field<long>(host, "_epoch"));
                PumpUntil(() => !Field<bool>(host, "_refreshing") && !Field<bool>(host, "_powerResumePending"));
                Assert.StartsWith("Ready; fresh audio census", Field<string>(host, "_powerStatus"));
                Assert.Equal(before, JsonSerializer.Serialize(settings));
            }
        }
        finally
        {
            var shutdown = host.ShutdownAsync();
            PumpUntil(() => shutdown.IsCompleted);
            shutdown.GetAwaiter().GetResult();
        }
    });

    [Fact]
    public void ResumeWithoutSuspendStillRefreshesAndUnrelatedMessagesDoNotResetTheHost() => OnSta(() =>
    {
        using var host = new TrayContext(diagnosticsMode: true);
        try
        {
            var initial = Field<long>(host, "_epoch");
            host.HandlePowerEvent(10); // Battery/AC status is not a sleep transition.
            host.HandlePowerEvent(7); // Duplicate/late user resume without a suspend.
            Assert.Equal(initial, Field<long>(host, "_epoch"));
            host.HandlePowerEvent(18);
            Assert.True(Field<long>(host, "_epoch") > initial);
            PumpUntil(() => !Field<bool>(host, "_refreshing") && !Field<bool>(host, "_powerResumePending"));
        }
        finally
        {
            var shutdown = host.ShutdownAsync();
            PumpUntil(() => shutdown.IsCompleted);
            shutdown.GetAwaiter().GetResult();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppleTransactionCannotSurviveACompletePowerCycle(bool afterWrite) => OnSta(() =>
    {
        using var host = new TrayContext(diagnosticsMode: true);
        var settings = Field<SourceSettings>(host, "_settings");
        settings.TargetEndpointId = "output";
        settings.SwitchingEnabled = true; // Diagnostics hosts start safely disabled.
        settings.SelectMaster(@"C:\PLAYER.EXE", "Player");
        var context = host.CreateAppleSwitchContext("output", settings.SelectedSourceKey!, settings.SelectionRevision);
        Assert.True(context());
        var elapsed = TimeSpan.Zero;
        var wrote = false;
        var cycled = false;
        var guard = new AppleMusicSwitchGuard((delay, _) =>
        {
            elapsed += delay;
            if (!cycled && (!afterWrite || wrote))
            {
                host.HandlePowerEvent(4);
                host.HandlePowerEvent(18);
                cycled = true;
            }
            return Task.CompletedTask;
        }, () => elapsed);
        try
        {
            var transaction = guard.RunAsync(new AdvancingAppleSession(), context, () => true, () => .1f,
                () => { wrote = true; return new(true, true, "Fixture native write"); }, default);
            PumpUntil(() => transaction.IsCompleted);
            var result = transaction.GetAwaiter().GetResult();
            Assert.True(cycled);
            Assert.False(context()); // It must remain invalid even though resume completed.
            Assert.Equal(afterWrite, wrote);
            Assert.Equal(afterWrite, result.Applied?.Success == true);
            Assert.False(result.PlaybackObserved);
        }
        finally
        {
            var shutdown = host.ShutdownAsync();
            PumpUntil(() => shutdown.IsCompleted);
            shutdown.GetAwaiter().GetResult();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifiedAppleFormatUpdatesVisibleOutputWhileRecoveryRemainsSerialized(bool staleOperation) => OnSta(() =>
    {
        using var host = new TrayContext(diagnosticsMode: true);
        PumpUntil(() => !Field<bool>(host, "_refreshing"));
        var settings = Field<SourceSettings>(host, "_settings");
        settings.SwitchingEnabled = true;
        settings.TargetEndpointId = "output";
        settings.SelectMaster(@"C:\PLAYER.EXE", "Player");
        var context = host.CreateAppleSwitchContext("output", settings.SelectedSourceKey!, settings.SelectionRevision);
        var popup = Field<SourcePopup>(host, "_popup");
        popup.Opacity = 0; popup.Show(); Application.DoEvents();
        SetField(host, "_formatOperationGeneration", 1L);
        SetField(host, "_refreshing", true);
        SetField(host, "_applying", true);
        try
        {
            host.PublishAppleFormatConfirmation(staleOperation ? 0 : 1, context, new(88200, 32, 2), new(88200, 32, 2));
            Assert.True(Field<bool>(host, "_refreshing")); // A second format write must still serialize.
            Assert.Equal(staleOperation, Field<bool>(host, "_applying"));
            Assert.Equal(!staleOperation, Descendants<Label>(popup).Any(label => label.Text.StartsWith("Shared output: 88.2 kHz")));
            if (!staleOperation)
            {
                Assert.True(Descendants<Button>(popup).Single(button => button.Text == "Release master").Enabled);
                Assert.Contains("observing", Field<string>(host, "_appleSwitchDetail"));
            }
        }
        finally
        {
            SetField(host, "_refreshing", false); SetField(host, "_applying", false);
            var shutdown = host.ShutdownAsync(); PumpUntil(() => shutdown.IsCompleted); shutdown.GetAwaiter().GetResult();
        }
    });

    [Theory]
    [InlineData("manual")]
    [InlineData("catalog")]
    [InlineData("stream")]
    public void AppleObservationCancelsWhenRatePreferencesChangeWithoutMasterRevision(string preference) => OnSta(() =>
    {
        using var host = new TrayContext(diagnosticsMode: true);
        PumpUntil(() => !Field<bool>(host, "_refreshing"));
        var settings = Field<SourceSettings>(host, "_settings");
        settings.SwitchingEnabled = true; settings.TargetEndpointId = "output";
        settings.SelectMaster(@"C:\PLAYER.EXE", "Player");
        var revision = settings.SelectionRevision;
        var context = host.CreateAppleSwitchContext("output", settings.SelectedSourceKey!, revision);
        Assert.True(context());
        if (preference == "manual") settings.ManualSampleRatesHz[settings.SelectedSourceKey!] = 96000;
        if (preference == "catalog") settings.CatalogRateDetectionEnabled = !settings.CatalogRateDetectionEnabled;
        if (preference == "stream") settings.StreamRateDetectionEnabled = !settings.StreamRateDetectionEnabled;
        host.InvalidateAppleSwitchPreferences(); // The UI mutation transaction calls this after updating settings.
        Assert.Equal(revision, settings.SelectionRevision);
        Assert.False(context());
        var shutdown = host.ShutdownAsync(); PumpUntil(() => shutdown.IsCompleted); shutdown.GetAwaiter().GetResult();
    });

    private static IEnumerable<T> Descendants<T>(Control control) where T : Control => control.Controls.Cast<Control>()
        .SelectMany(child => (child is T value ? new[] { value } : []).Concat(Descendants<T>(child)));
    private static void SetField<T>(TrayContext host, string name, T value) => typeof(TrayContext)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);

    private sealed class AdvancingAppleSession : IAppleSwitchSession
    {
        private TimeSpan _position;
        public Task<AppleSwitchState?> ReadAsync(CancellationToken token)
        {
            _position += TimeSpan.FromMilliseconds(100);
            return Task.FromResult<AppleSwitchState?>(new("track", MediaTransportPlaybackStatus.Playing, _position));
        }
    }

    private static T Field<T>(TrayContext host, string name) => (T)typeof(TrayContext)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    private static void PumpUntil(Func<bool> completed)
    {
        var watch = Stopwatch.StartNew();
        do { Application.DoEvents(); if (completed()) return; Thread.Sleep(5); }
        while (watch.Elapsed < TimeSpan.FromSeconds(10));
        throw new TimeoutException("Power recovery did not settle.");
    }
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Power test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
