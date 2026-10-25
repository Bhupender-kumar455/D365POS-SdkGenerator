namespace D365SdkGenerator.Services;

public enum GeneratorLogLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record GeneratorLogEntry(
    DateTimeOffset Timestamp,
    GeneratorLogLevel Level,
    string Message);

/// <summary>
/// The generation pipeline is UI-agnostic. It reports progress through this interface so
/// the desktop app can stream status into its activity pane, while tests can run silently.
/// </summary>
public interface IGeneratorLog
{
    void Write(GeneratorLogLevel level, string message);
}

public static class GeneratorLogExtensions
{
    public static void Info(this IGeneratorLog log, string message) =>
        log.Write(GeneratorLogLevel.Info, message);

    public static void Success(this IGeneratorLog log, string message) =>
        log.Write(GeneratorLogLevel.Success, message);

    public static void Warning(this IGeneratorLog log, string message) =>
        log.Write(GeneratorLogLevel.Warning, message);

    public static void Error(this IGeneratorLog log, string message) =>
        log.Write(GeneratorLogLevel.Error, message);
}

public sealed class NullGeneratorLog : IGeneratorLog
{
    public static readonly NullGeneratorLog Instance = new();

    private NullGeneratorLog()
    {
    }

    public void Write(GeneratorLogLevel level, string message)
    {
    }
}

public sealed class DelegateGeneratorLog : IGeneratorLog
{
    private readonly Action<GeneratorLogEntry> sink;
    private readonly TimeProvider clock;

    public DelegateGeneratorLog(Action<GeneratorLogEntry> sink, TimeProvider? clock = null)
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.clock = clock ?? TimeProvider.System;
    }

    public void Write(GeneratorLogLevel level, string message) =>
        sink(new GeneratorLogEntry(clock.GetUtcNow(), level, message));
}
