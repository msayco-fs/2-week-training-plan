namespace InvoiceReporting.Api.Security;

public static class RateLimitPolicies
{
    /// <summary>
    /// Fixed window per authenticated subject. Anonymous callers are partitioned
    /// by remote IP address. Limits come from <see cref="PerCallerRateLimitOptions"/>.
    /// </summary>
    public const string PerCaller = "PerCaller";
}
