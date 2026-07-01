public sealed class ManualUtcTimeProvider(DateTimeOffset initial) : System.TimeProvider
{
    private DateTimeOffset _now = initial;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Set(DateTimeOffset value) => _now = value;   // no forward-only restriction (unlike FakeTimeProvider)
}
