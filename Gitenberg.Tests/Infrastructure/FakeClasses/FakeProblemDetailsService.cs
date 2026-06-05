using Microsoft.AspNetCore.Http;

namespace Gitenberg.Tests.Infrastructure.FakeClasses;

public class FakeProblemDetailsService : IProblemDetailsService
{
    public ProblemDetailsContext? WrittenContext { get; private set; }

    public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
    {
        WrittenContext = context;
        return ValueTask.FromResult(true);
    }

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        WrittenContext = context;
        return ValueTask.CompletedTask;
    }
}