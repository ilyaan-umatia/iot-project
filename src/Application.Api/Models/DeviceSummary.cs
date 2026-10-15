namespace Application.Api.Models;

public sealed record DeviceSummary(string DeviceId, long Readings, long FirstSeq, long LastSeq,
    long Missing, long Duplicates, long Historical);
