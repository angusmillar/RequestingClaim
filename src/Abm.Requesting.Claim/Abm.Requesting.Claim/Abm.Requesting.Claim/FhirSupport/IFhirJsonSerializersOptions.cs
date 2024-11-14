using System.Text.Json;
using Hl7.Fhir.Rest;

namespace Abm.Requesting.Claim.FhirSupport;

public interface IFhirJsonSerializersOptions
{
  JsonSerializerOptions ForDeserialization();
  JsonSerializerOptions ForSerialization(SummaryType? summaryType, bool pretty = false);
}
