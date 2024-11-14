
namespace Abm.Requesting.Claim.Settings;

public sealed class WebAppSettings
{
    public const string SectionName = "Settings";
    
    public required string DefaultFhirRepositoryCode { get; init; }
    
    public required Uri GroupTaskTagSystem { get; init; }
    
    public required string GroupTaskTagCode { get; init; } 
    
}