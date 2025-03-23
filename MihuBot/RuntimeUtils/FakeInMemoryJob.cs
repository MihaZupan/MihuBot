namespace MihuBot.RuntimeUtils;

public sealed class FakeInMemoryJob : JobBase
{
    public override string JobTitlePrefix => "Fake";

    public FakeInMemoryJob(RuntimeUtilsService parent, string githubCommenterLogin) : base(parent, githubCommenterLogin, "Fake args")
    {
        SuppressTrackingIssue = true;
    }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        int counter = 0;

        RemoteLoginCredentials = "foo@127.0.0.1 bar";

        while (await timer.WaitForNextTickAsync(jobTimeout))
        {
            LastProgressSummary = new string('a', Random.Shared.Next(5, 20));
            LastSystemInfo = new SystemHardwareInfo(Random.Shared.NextDouble() * 16, 16, Random.Shared.NextDouble() * 64, 64);
            Log($"Dummy message {++counter} {new string('a', Random.Shared.Next(50, 500))}");
        }
    }
}
