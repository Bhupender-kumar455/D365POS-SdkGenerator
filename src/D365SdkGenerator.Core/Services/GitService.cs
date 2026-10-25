using System.Diagnostics;
using System.Text;

namespace D365SdkGenerator.Services;

public sealed class GitService
{
    public async Task InitAsync(
        string directory,
        CancellationToken cancellationToken = default) =>
        await RunProcessAsync(
            "git",
            $"-C \"{directory}\" init",
            cancellationToken);

    private static async Task RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command failed: {fileName} {arguments}{Environment.NewLine}" +
                stderrTask.Result + stdoutTask.Result);
        }
    }
}
