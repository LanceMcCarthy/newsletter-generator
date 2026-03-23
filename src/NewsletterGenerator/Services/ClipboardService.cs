using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NewsletterGenerator.Services;

internal enum ClipboardPlatform
{
    Unsupported,
    Windows,
    Linux,
    MacOS
}

internal readonly record struct ClipboardCommand(string FileName, params string[] Arguments);

internal delegate Task<bool> ClipboardCommandRunner(ClipboardCommand command, string text);

internal sealed class ClipboardService
{
    private readonly ILogger<ClipboardService> logger;
    private readonly Func<ClipboardPlatform> platformResolver;
    private readonly ClipboardCommandRunner commandRunner;

    internal ClipboardService(
        ILogger<ClipboardService> logger,
        Func<ClipboardPlatform>? platformResolver = null,
        ClipboardCommandRunner? commandRunner = null)
    {
        this.logger = logger;
        this.platformResolver = platformResolver ?? GetCurrentPlatform;
        this.commandRunner = commandRunner ?? TryClipboardCommandAsync;
    }

    /// <summary>
    /// Attempts to copy the provided text to the system clipboard.
    /// Returns true if successful, false otherwise.
    /// </summary>
    public async Task<bool> TrySetClipboardTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            logger.LogWarning("Cannot copy empty or null text to clipboard");
            return false;
        }

        var commands = GetClipboardCommands(platformResolver());
        if (commands.Count == 0)
        {
            logger.LogWarning("Clipboard operations are not supported on this platform");
            return false;
        }

        foreach (var command in commands)
        {
            if (await commandRunner(command, text))
            {
                return true;
            }
        }

        logger.LogWarning("Failed to copy text to clipboard using any supported command for this platform");
        return false;
    }

    private static IReadOnlyList<ClipboardCommand> GetClipboardCommands(ClipboardPlatform platform)
    {
        return platform switch
        {
            ClipboardPlatform.Windows => [new ClipboardCommand("clip.exe")],
            ClipboardPlatform.Linux =>
            [
                new ClipboardCommand("xclip", "-selection", "clipboard"),
                new ClipboardCommand("xsel", "--clipboard", "--input")
            ],
            ClipboardPlatform.MacOS => [new ClipboardCommand("pbcopy")],
            _ => []
        };
    }

    private static ClipboardPlatform GetCurrentPlatform()
    {
        return true switch
        {
            _ when OperatingSystem.IsWindows() => ClipboardPlatform.Windows,
            _ when OperatingSystem.IsLinux() => ClipboardPlatform.Linux,
            _ when OperatingSystem.IsMacOS() => ClipboardPlatform.MacOS,
            _ => ClipboardPlatform.Unsupported
        };
    }

    private async Task<bool> TryClipboardCommandAsync(ClipboardCommand command, string text)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command.FileName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in command.Arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi);
            if (process == null)
            {
                logger.LogWarning("Failed to start clipboard command {Command}", command.FileName);
                return false;
            }

            var errorTask = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(text);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();

            await process.WaitForExitAsync();
            var error = await errorTask;

            if (process.ExitCode == 0)
            {
                logger.LogDebug("Successfully copied {Length} characters to clipboard using {Command}", text.Length, command.FileName);
                return true;
            }

            logger.LogWarning("Clipboard command {Command} failed with exit code {ExitCode}: {Error}", command.FileName, process.ExitCode, error);
            return false;
        }
        catch (Win32Exception ex)
        {
            logger.LogDebug(ex, "Clipboard command {Command} is unavailable", command.FileName);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Clipboard command {Command} could not be started or accessed", command.FileName);
            return false;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "I/O error while running clipboard command {Command}", command.FileName);
            return false;
        }
    }
}