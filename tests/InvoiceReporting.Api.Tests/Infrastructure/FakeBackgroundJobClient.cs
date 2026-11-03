using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace InvoiceReporting.Api.Tests.Infrastructure;

/// <summary>Records queued jobs instead of storing them, so tests can check what was queued.</summary>
public sealed class FakeBackgroundJobClient : IBackgroundJobClient
{
    public List<Job> Created { get; } = [];

    public string Create(Job job, IState state)
    {
        Created.Add(job);
        return Created.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool ChangeState(string jobId, IState state, string expectedState) => true;
}
