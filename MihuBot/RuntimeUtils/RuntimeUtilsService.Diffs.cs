namespace MihuBot.RuntimeUtils;

public sealed partial class RuntimeUtilsService
{
    public async Task<(string Title, DiffExamplesReport[] Reports, CompletedJobRecord.Artifact RegexResults)> GetDiffExamplesAsync(string externalId, CancellationToken cancellationToken)
    {
        if (!DiffExamplesReport.IsPublicId(externalId))
        {
            throw new ArgumentException("Invalid public job ID.", nameof(externalId));
        }

        RuntimeUtilsJobStatusResponse status = await TryGetJobStatusAsync(externalId, cancellationToken);
        if (status is null)
        {
            return (null, [], null);
        }

        var reports = new List<DiffExamplesReport>();
        foreach (string name in new[] { DiffExamplesReport.JitArtifactName, DiffExamplesReport.RegexArtifactName })
        {
            var artifact = status.Artifacts.FirstOrDefault(a => a.FileName == name);
            if (artifact is null)
            {
                continue;
            }

            if (artifact.Size > DiffExamplesReport.MaxArtifactBytes)
            {
                throw new InvalidDataException("Diff report exceeds the size limit.");
            }

            // Resolve only known names under the public ID; artifact URLs are never fetched.
            if (!Storage.TryGetLocalFilePath(ArtifactsStorage.ContainerName, $"{externalId}/{name}", out string path))
            {
                throw new FileNotFoundException("A diff report artifact is no longer available.");
            }

            await using FileStream stream = File.OpenRead(path);
            if (stream.Length > DiffExamplesReport.MaxArtifactBytes)
            {
                throw new InvalidDataException("Diff report exceeds the size limit.");
            }

            reports.Add(await DiffExamplesReport.ReadAsync(stream, cancellationToken));
        }

        var regexResults = status.Artifacts.Any(a => a.FileName == DiffExamplesReport.RegexArtifactName)
            ? status.Artifacts.FirstOrDefault(a => a.FileName == "Results.zip")
            : null;

        return (status.Title, [.. reports], regexResults);
    }
}
