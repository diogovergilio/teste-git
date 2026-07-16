namespace DashCall.Collector.Net;

public static class ReconnectPolicy
{
    public static TimeSpan DelayFor(int attempt)
    {
        var seconds = Math.Min(30, Math.Pow(2, Math.Min(attempt, 30)));
        return TimeSpan.FromSeconds(seconds);
    }
}
