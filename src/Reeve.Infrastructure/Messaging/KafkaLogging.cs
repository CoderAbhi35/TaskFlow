using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Reeve.Infrastructure.Messaging;

/// <summary>
/// Routes librdkafka's own logs through <see cref="ILogger"/>. Without this they go straight to
/// stderr, bypassing log levels, structured output and the logging pipeline.
/// </summary>
public static class KafkaLogging
{
    public static void Write(ILogger logger, LogMessage message) =>
        logger.Log(ToLogLevel(message.Level), "Kafka client {ClientName} [{Facility}]: {Message}",
            message.Name, message.Facility, message.Message);

    public static LogLevel ToLogLevel(SyslogLevel level) => level switch
    {
        SyslogLevel.Emergency or SyslogLevel.Alert or SyslogLevel.Critical => LogLevel.Critical,
        SyslogLevel.Error => LogLevel.Error,
        SyslogLevel.Warning => LogLevel.Warning,
        SyslogLevel.Notice or SyslogLevel.Info => LogLevel.Information,
        _ => LogLevel.Debug,
    };
}
