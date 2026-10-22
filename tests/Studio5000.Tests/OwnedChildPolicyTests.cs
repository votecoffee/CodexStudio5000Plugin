using System.Reflection;
using System.Runtime.InteropServices;
using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class OwnedChildPolicyTests
{
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("simple", "simple")]
    [InlineData("has spaces", "\"has spaces\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("C:\\space name\\", "\"C:\\space name\\\\\"")]
    public void Argument_quoting_preserves_windows_boundaries(string value, string expected) =>
        Assert.Equal(expected, WindowsChildProcess.CommandLine([value]));
    [Fact]
    public void Invalid_arguments_are_rejected_before_any_child_or_intent()
    {
        Assert.Throws<ArgumentException>(() => WindowsChildProcess.CommandLine([]));
        Assert.Throws<ArgumentException>(() => WindowsChildProcess.CommandLine(["contains\0nul"]));
        Assert.Throws<ArgumentException>(() => WindowsChildProcess.CommandLine([new string('a', 32767)]));
    }
    [Fact]
    public void Native_job_and_stream_structures_match_windows_layout_without_launching()
    {
        int Size(string name) => Marshal.SizeOf(typeof(WindowsChildProcess).GetNestedType(name, BindingFlags.NonPublic)!);
        Assert.Equal(IntPtr.Size == 8 ? 24 : 12, Size("SecurityAttributes"));
        Assert.Equal(IntPtr.Size == 8 ? 104 : 68, Size("StartupInfo"));
        Assert.Equal(IntPtr.Size == 8 ? 112 : 72, Size("StartupInfoEx"));
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Size("ProcessInfo"));
        Assert.Equal(IntPtr.Size == 8 ? 144 : 112, Size("ExtendedLimits"));
    }
}
