using System.Text.RegularExpressions;

namespace InvoiceReporting.Api.Tests.Reporting;

/// <summary>
/// The API is async end to end. This scans the backend source and fails on
/// calls that block a thread on a task (<c>.Wait()</c>, <c>GetAwaiter().GetResult()</c>,
/// <c>.Result</c> on a task), hide sync work behind <c>Task.Run</c>, or on
/// <c>async void</c> methods.
/// </summary>
public sealed partial class NoBlockingCallsTests
{
    [Fact]
    public void BackendSource_HasNoBlockingTaskCalls()
    {
        var backend = Path.Combine(RepositoryRoot(), "backend", "InvoiceReporting.Api");

        var hits = Directory.EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGenerated(path))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(x => BlockingCall().IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(backend, x.path)}:{x.number}: {x.line.Trim()}")
            .ToList();

        Assert.Empty(hits);
    }

    [Theory]
    [InlineData("var x = GetAsync(id).Result;")]
    [InlineData("task.Result")]
    [InlineData("SaveTask.Result")]
    [InlineData("job.Wait();")]
    [InlineData("RunAsync().GetAwaiter().GetResult();")]
    [InlineData("await Task.Run(() => Work());")]
    [InlineData("public async void Handle()")]
    public void Pattern_FindsBlockingCalls(string line) => Assert.Matches(BlockingCall(), line);

    [Theory]
    [InlineData("switch (claim.Result)")]
    [InlineData("var authorization = await authorizationService.AuthorizeAsync(User, scope, policy);")]
    [InlineData("await Task.Delay(delay, cancellationToken);")]
    public void Pattern_IgnoresNonBlockingCode(string line) => Assert.DoesNotMatch(BlockingCall(), line);

    private static bool IsGenerated(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains("obj") || parts.Contains("bin") || parts.Contains("Migrations");
    }

    // A ".Result" is only flagged on something that is clearly a task (a
    // *Async(...) call or a name ending in "task"), so properties named
    // Result, like EmailDeliveryClaim.Result, are not reported.
    [GeneratedRegex(@"(Async\([^;]*\)|[Tt]ask)\.Result\b|\.Wait\(\)|GetAwaiter\(\)\.GetResult\(\)|\bTask\.Run\(|\basync\s+void\b")]
    private static partial Regex BlockingCall();

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "InvoiceReporting.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("InvoiceReporting.slnx not found above the test output folder.");
    }
}
