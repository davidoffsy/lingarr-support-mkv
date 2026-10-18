namespace Lingarr.Server.Models.Api;

public class ProbeEmbeddedResponse
{
    public bool Found { get; set; }
    public string? Reason { get; set; }
    public string? FileName { get; set; }
    public List<ProbeEmbeddedTrackDto> Tracks { get; set; } = [];
    public List<SkippedEmbeddedTrackDto> Skipped { get; set; } = [];
}

public class ProbeEmbeddedTrackDto
{
    public int Index { get; set; }
    public string Language { get; set; } = string.Empty;
    public string CodecName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
    public bool IsHearingImpaired { get; set; }
    public bool CanAutoTranslate { get; set; }
    public bool SidecarExists { get; set; }
    public bool Extractable { get; set; } = true;
}

public class SkippedEmbeddedTrackDto
{
    public int Index { get; set; }
    public string CodecName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public bool IsForced { get; set; }
}
