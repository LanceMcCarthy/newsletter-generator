using Microsoft.Extensions.Logging.Abstractions;
using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class ClipboardServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task TrySetClipboardTextAsync_ReturnsFalse_ForEmptyInput(string? text)
    {
        var callCount = 0;
        var service = CreateService(
            () => ClipboardPlatform.Windows,
            (_, _) =>
            {
                callCount++;
                return Task.FromResult(true);
            });

        var result = await service.TrySetClipboardTextAsync(text!);

        Assert.False(result);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task TrySetClipboardTextAsync_ReturnsFalse_ForUnsupportedPlatform()
    {
        var callCount = 0;
        var service = CreateService(
            () => ClipboardPlatform.Unsupported,
            (_, _) =>
            {
                callCount++;
                return Task.FromResult(true);
            });

        var result = await service.TrySetClipboardTextAsync("newsletter content");

        Assert.False(result);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task TrySetClipboardTextAsync_UsesWindowsClipboardCommand()
    {
        var commands = new List<ClipboardCommand>();
        var service = CreateService(
            () => ClipboardPlatform.Windows,
            (command, _) =>
            {
                commands.Add(command);
                return Task.FromResult(true);
            });

        var result = await service.TrySetClipboardTextAsync("newsletter content");

        Assert.True(result);
        var command = Assert.Single(commands);
        Assert.Equal("clip.exe", command.FileName);
        Assert.Empty(command.Arguments);
    }

    [Fact]
    public async Task TrySetClipboardTextAsync_FallsBackToSecondLinuxCommand()
    {
        var commands = new List<ClipboardCommand>();
        var service = CreateService(
            () => ClipboardPlatform.Linux,
            (command, _) =>
            {
                commands.Add(command);
                return Task.FromResult(command.FileName == "xsel");
            });

        var result = await service.TrySetClipboardTextAsync("newsletter content");

        Assert.True(result);
        Assert.Equal(2, commands.Count);
        Assert.Equal("xclip", commands[0].FileName);
        Assert.Equal(["-selection", "clipboard"], commands[0].Arguments);
        Assert.Equal("xsel", commands[1].FileName);
        Assert.Equal(["--clipboard", "--input"], commands[1].Arguments);
    }

    [Fact]
    public async Task TrySetClipboardTextAsync_ReturnsFalse_WhenAllCommandsFail()
    {
        var service = CreateService(
            () => ClipboardPlatform.MacOS,
            (_, _) => Task.FromResult(false));

        var result = await service.TrySetClipboardTextAsync("newsletter content");

        Assert.False(result);
    }

    private static ClipboardService CreateService(
        Func<ClipboardPlatform> platformResolver,
        ClipboardCommandRunner commandRunner)
    {
        return new ClipboardService(
            NullLogger<ClipboardService>.Instance,
            platformResolver,
            commandRunner);
    }
}