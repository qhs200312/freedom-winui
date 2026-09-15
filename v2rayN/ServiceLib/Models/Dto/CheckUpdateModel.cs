namespace ServiceLib.Models.Dto;

public class CheckUpdateModel : ReactiveObject
{
    public bool? IsSelected { get; set; }
    public ECoreType? CoreType { get; set; }
    [Reactive] public string? Remarks { get; set; }
    public string? FileName { get; set; }
    public bool? IsFinished { get; set; }
    public bool IsGeoFile { get; set; }
    public string CoreTypeForStorage => IsGeoFile ? "GeoFiles" : (CoreType?.ToString() ?? "");
    public string DisplayName => CoreType == ECoreType.v2rayN && !IsGeoFile ? Global.AppName : CoreTypeForStorage;
}
