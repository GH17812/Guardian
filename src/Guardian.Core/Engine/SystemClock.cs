using Guardian.Core.Ports;

namespace Guardian.Core.Engine;

/// <summary>默认系统时钟。</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
