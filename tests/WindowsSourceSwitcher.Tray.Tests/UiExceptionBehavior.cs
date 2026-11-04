using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace WindowsSourceSwitcher.Tray.Tests;

internal static class UiExceptionBehavior
{
    [ModuleInitializer]
    internal static void FailTestsInsteadOfShowingDesktopDialogs() =>
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: false);
}
