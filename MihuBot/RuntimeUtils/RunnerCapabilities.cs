namespace MihuBot.RuntimeUtils;

public sealed record RunnerCapabilities(string JobType, string Os, string Architecture, string BaseRepo, string BaseBranch)
{
    public bool IsCompatibleWith(RunnerCapabilities runner)
    {
        return string.Equals(JobType, runner.JobType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Os, runner.Os, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Architecture, runner.Architecture, StringComparison.OrdinalIgnoreCase)
            && string.Equals(BaseRepo, runner.BaseRepo, StringComparison.OrdinalIgnoreCase)
            && string.Equals(BaseBranch, runner.BaseBranch, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => $"{JobType} {Os}/{Architecture}, {BaseRepo}@{BaseBranch}";
}
