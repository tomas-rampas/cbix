namespace Cbix.Core.Workflows;

/// <summary>
/// The workflow's input: one document offered to the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately just a location and a media type. Everything else about the document - its identity,
/// whether it has been seen before, its text layer, how it will be presented to a model - is derived
/// by the ingest executor from the bytes, never taken from the submitter (design 5.1). A submission
/// that carried, say, a claimed content hash would be a claim the pipeline had to either trust or
/// re-check, and re-checking makes the field pointless.
/// </para>
/// <para>
/// <see cref="DocumentPath"/> is not validated here. Containment - that the path resolves inside the
/// configured ingest root, through no link, hard link, junction or UNC prefix - is the ingest gate's
/// job and is enforced there, on the resolved path, immediately before the file is opened. A shape
/// check on this record would be a second, weaker copy of that rule and would invite the belief that
/// a constructed submission is already safe.
/// </para>
/// </remarks>
/// <param name="DocumentPath">
/// Where the document is, absolute or relative to the configured ingest root.
/// </param>
/// <param name="MediaType">IANA media type of the document bytes.</param>
public sealed record DocumentSubmission(string DocumentPath, string MediaType = "application/pdf");
