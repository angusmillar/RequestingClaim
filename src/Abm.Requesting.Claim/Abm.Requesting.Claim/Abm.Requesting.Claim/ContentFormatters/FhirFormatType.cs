using Abm.Requesting.Claim.Attributes;

namespace Abm.Requesting.Claim.ContentFormatters;

public enum FhirFormatType 
{
    [EnumInfo("json", "application/fhir+json" )]
    Json,
    [EnumInfo("xml", "application/fhir+xml")]
    Xml    
};