using System.Net;
using Abm.Requesting.Claim.Operation;
using Hl7.Fhir.Model;
using Hl7.Fhir.Utility;
using Microsoft.AspNetCore.Mvc;
using Task = System.Threading.Tasks.Task;

namespace Abm.Requesting.Claim.Controllers;

[Route("pyro")]
[ApiController]
public class FhirController(IClaimOperation claimOperation) : ControllerBase
{
  [HttpPost]
  public async Task<ActionResult<Resource>> Base(string tenant, [FromBody]Resource resource, CancellationToken cancellationToken)
  {
    //Should return a metadata CapabilityStatement
    await Task.Delay(1000, cancellationToken);
    return  StatusCode((int)HttpStatusCode.OK, new CapabilityStatement() {Id = "test"});
    
  }
  
  [HttpPost("{resourceName}/{operationName}")]
  public async Task<ActionResult<Resource>> Post(string resourceName, string operationName, [FromBody]Resource resource, CancellationToken cancellationToken)
  {
    if (!resourceName.Equals(ResourceType.ServiceRequest.GetLiteral(), StringComparison.Ordinal))
    {
      throw new ArgumentNullException(nameof(resourceName));
    }
    if (!operationName.Equals("$claim", StringComparison.Ordinal))
    {
      throw new ArgumentNullException(nameof(resourceName));
    }

    if (resource is not Parameters parameters)
    {
      throw new ArgumentNullException(nameof(resourceName));
    }

    ClaimOperationOutcome outcome =
      await claimOperation.Process(claimParameters: parameters, cancellationToken: cancellationToken);
    
    return  StatusCode((int)outcome.HttpStatusCode, outcome.Resource);
  }
  
}
