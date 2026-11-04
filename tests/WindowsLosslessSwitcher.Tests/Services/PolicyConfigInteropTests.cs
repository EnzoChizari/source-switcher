using System.Reflection;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class PolicyConfigInteropTests
{
    [Fact]
    public void AllPolicyConfigMethodsPreserveNativeHResultSignature()
    {
        // This is a metadata-only regression: no COM activation or endpoint changes.
        // Omitting PreserveSig silently changes the native HRESULT ABI for these int methods.
        var contract = typeof(PolicyConfigInterop).GetNestedType("IPolicyConfig", BindingFlags.NonPublic);
        Assert.NotNull(contract);
        var methods = contract!.GetMethods();
        Assert.NotEmpty(methods);
        Assert.All(methods, method =>
        {
            Assert.Equal(typeof(int), method.ReturnType);
            Assert.True((method.MethodImplementationFlags & MethodImplAttributes.PreserveSig) != 0,
                $"{method.Name} must preserve the native HRESULT signature.");
        });
    }
}
