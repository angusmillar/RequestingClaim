using System.Net;
using Abm.Requesting.Claim.FhirSupport;
using Abm.Requesting.Claim.Settings;
using FhirNavigator;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Options;

namespace Abm.Requesting.Claim.Operation;

public class ClaimOperation(
    ILogger<ClaimOperation> logger,
    IOptions<WebAppSettings> webAppSettings,
    IOperationOutcomeSupport operationOutcomeSupport,
    IFhirNavigatorFactory fhirNavigatorFactory) : IClaimOperation
{
    private IFhirNavigator? _fhirNavigator;
    private readonly List<string> _errorMessageList = new();
    private const string RequisitionParameterName = "requisition";
    private const string OrganizationParameterName = "organization";
    private const string ResultParameterName = "result";
    private const string GroupTask = "groupTask";

    private readonly Uri _requestClaimResultTypeSystemUri =
        new Uri("http://fhir.geniesolutions.io/CodeSystem/eorder-claim-result-type-codesystem");

    private readonly Hl7.Fhir.Model.Task.TaskStatus[] _claimableTaskStatusList =
    [
        //See: https://hl7.org/fhir/R4/task.html#statemachine
        Hl7.Fhir.Model.Task.TaskStatus.Ready,
        Hl7.Fhir.Model.Task.TaskStatus.Requested,
        Hl7.Fhir.Model.Task.TaskStatus.Received,
        Hl7.Fhir.Model.Task.TaskStatus.Accepted,
        Hl7.Fhir.Model.Task.TaskStatus.Rejected,
    ];

    public async Task<ClaimOperationOutcome> Process(
        Parameters claimParameters,
        CancellationToken cancellationToken)
    {
        ClaimRequest? claimRequest = GetValidClaimRequest(claimParameters);
        if (claimRequest == null)
        {
            LogErrorMessage("[Unknown]");
            return GetErrorClaimOperationOutcome();
        }

        LogIncommingClaimRequest(claimRequest);

        _fhirNavigator = fhirNavigatorFactory.GetFhirNavigator(webAppSettings.Value.DefaultFhirRepositoryCode);

        Organization? targetFillerOrganization = await GetOrganization(claimRequest.organization);
        if (targetFillerOrganization == null)
        {
            _errorMessageList.Add($"Claim failed as no claimant Organization resource was not found");
            LogErrorMessage(claimRequest.requisition.Value);
            return GetOrganizationNotFoundClaimOperationOutcome();
        }

        logger.LogInformation("{Requisition}: Found claimant organization {OrganizationName}, with resource id {OrganizationId}",
            claimRequest.requisition.Value,
            targetFillerOrganization.Id, 
            targetFillerOrganization.Name);
        
        List<Hl7.Fhir.Model.Task>? requestTaskResourceList = await GetRequestTaskResourceList(claimRequest.requisition);
        if (requestTaskResourceList == null)
        {
            _errorMessageList.Add($"Claim failed as no Tasks resources found for the provided requisition " +
                                  $"identifier: {claimRequest.requisition.System}|{claimRequest.requisition.Value}");
            LogErrorMessage(claimRequest.requisition.Value);
            return GetRequisitionNotFoundClaimOperationOutcome();
        }

        logger.LogInformation("{Requisition}: Found target request Task resources", claimRequest.requisition.Value);
        
        List<Hl7.Fhir.Model.Task>? requestTaskList = ValidateRequestIsClaimable(requestTaskResourceList);
        if (requestTaskList == null)
        {
            LogErrorMessage(claimRequest.requisition.Value);
            return GetErrorClaimOperationOutcome();
        }

        logger.LogInformation("{Requisition}: Is Claimable", claimRequest.requisition.Value);
        
        Hl7.Fhir.Model.Task? groupTask = GetRequestGroupTask(requestTaskList);
        if (groupTask is null)
        {
            LogErrorMessage(claimRequest.requisition.Value);
            return GetErrorClaimOperationOutcome();
        }
        logger.LogInformation("{Requisition}: Found Group Task resource", claimRequest.requisition.Value);
        
        Bundle cancelTaskTransactionBundle = GetCancelTaskTransactionBundle(requestTaskList);
        await _fhirNavigator.Transaction(cancelTaskTransactionBundle);

        logger.LogInformation("{Requisition}: Previous placer's Tasks Cancelled", claimRequest.requisition.Value);
        
        Bundle claimedTaskTransactionBundle = GetClaimedTaskTransactionBundle(
            requestTaskList: requestTaskList,
            targetOrganizationResourceId: targetFillerOrganization.Id,
            targetOrganizationName: targetFillerOrganization.Name);

        claimedTaskTransactionBundle = await _fhirNavigator.Transaction(claimedTaskTransactionBundle);
        groupTask = GetRequestGroupTaskFromTaskBundle(claimedTaskTransactionBundle.Entry);

        logger.LogInformation("{Requisition}: Request claimed successfully", claimRequest.requisition.Value);
        logger.LogInformation("{Requisition}: New Group Task ID {NewGroupTaskResourceId}", claimRequest.requisition.Value, groupTask.Id);
        return GetSuccessfulClaimOperationOutcome(groupTask.Id);
    }

    private Hl7.Fhir.Model.Task GetRequestGroupTaskFromTaskBundle(List<Bundle.EntryComponent> entryComponentList)
    {
        string tagSystem = webAppSettings.Value.GroupTaskTagSystem.OriginalString;
        string tagCode = webAppSettings.Value.GroupTaskTagCode;
        
        foreach (var entryComponent in entryComponentList)
        {
            if (entryComponent.Resource.Meta.Tag.Any(c =>
                    c.System.Equals(tagSystem, StringComparison.OrdinalIgnoreCase) &&
                    c.Code.Equals(tagCode, StringComparison.OrdinalIgnoreCase)))
            {
                if (entryComponent.Resource is Hl7.Fhir.Model.Task groupTask)
                {
                    return groupTask;
                }
            }
        }
        
        throw new ApplicationException("No group task found in returned found claim transaction bundle");
        
    }

    private void LogErrorMessage(string requisitionValue)
    {
        foreach (var message in _errorMessageList)
        {
            logger.LogInformation("{Requisition}: {Message}", requisitionValue, message);
        }
    }

    private void LogIncommingClaimRequest(
        ClaimRequest claimRequest)
    {
        if (!string.IsNullOrWhiteSpace(claimRequest.organization.Reference))
        {
            logger.LogInformation("{Requisition}: Organisation {OrganizationReference} claim for requisition: {RequisitionSystemValue}",
                claimRequest.requisition.Value,
                claimRequest.organization.Reference,
                $"{claimRequest.requisition.System} | {claimRequest.requisition.Value}");
            return;
        }
        
        logger.LogInformation("{Requisition}: Organisation {OrganizationReference} claim for requisition: {RequisitionSystemValue}",
            claimRequest.requisition.Value,
            $"{claimRequest.organization.Identifier.System} | {claimRequest.organization.Identifier.Value}",
            claimRequest.requisition.Value);
    }

    private Hl7.Fhir.Model.Task? GetRequestGroupTask(
        List<Hl7.Fhir.Model.Task> requestTaskList)
    {
        string tagSystem = webAppSettings.Value.GroupTaskTagSystem.OriginalString;
        string tagCode = webAppSettings.Value.GroupTaskTagCode;

        var groupTask = requestTaskList.SingleOrDefault(x =>
            x.Meta.Tag.Any(c =>
                c.System.Equals(tagSystem, StringComparison.OrdinalIgnoreCase) &&
                c.Code.Equals(tagCode, StringComparison.OrdinalIgnoreCase)));

        if (groupTask is null)
        {
            var groupTaskCount = requestTaskList.Count(x => x.Meta.Tag.Any(c =>
                c.System.Equals(tagSystem, StringComparison.OrdinalIgnoreCase) &&
                c.Code.Equals(tagCode, StringComparison.OrdinalIgnoreCase)));

            _errorMessageList.Add(
                $"The request can not be claimed as none, or many, Group Tasks resources were found for the request. " +
                $"Group Task resources found: {groupTaskCount}");
            return null;
        }

        return groupTask;
    }

    private Bundle GetClaimedTaskTransactionBundle(
        List<Hl7.Fhir.Model.Task> requestTaskList,
        string targetOrganizationResourceId,
        string targetOrganizationName)
    {
        Bundle bundle = new Bundle();
        bundle.Type = Bundle.BundleType.Transaction;
        bundle.Timestamp = DateTimeOffset.UtcNow;
        bundle.Entry = new List<Bundle.EntryComponent>();
        foreach (var task in requestTaskList)
        {
            bundle.Entry.Add(GetClaimedEntryComponent(task, targetOrganizationResourceId, targetOrganizationName));
        }

        return bundle;
    }

    private Bundle.EntryComponent GetClaimedEntryComponent(
        Hl7.Fhir.Model.Task task,
        string targetOrganizationResourceId,
        string targetOrganizationName)
    {
        task.Id = null;
        task.Status = Hl7.Fhir.Model.Task.TaskStatus.Requested;
        task.BusinessStatus = null;
        task.Owner = new ResourceReference($"{ResourceType.Organization.GetLiteral()}/{targetOrganizationResourceId}",
            display: targetOrganizationName);

        var component = new Bundle.EntryComponent()
        {
            Request = new Bundle.RequestComponent()
            {
                Method = Bundle.HTTPVerb.POST,
                Url = $"{task.TypeName}"
            }
        };
        component.FullUrl = $"urn:uuid:{Guid.NewGuid():D}";
        component.Resource = task;
        return component;
    }

    private Bundle GetCancelTaskTransactionBundle(
        List<Hl7.Fhir.Model.Task> requestTaskList)
    {
        Bundle bundle = new Bundle();
        bundle.Type = Bundle.BundleType.Transaction;
        bundle.Timestamp = DateTimeOffset.UtcNow;
        bundle.Entry = new List<Bundle.EntryComponent>();
        foreach (var task in requestTaskList)
        {
            bundle.Entry.Add(GetCancelEntryComponent(task));
        }

        return bundle;
    }

    private Bundle.EntryComponent GetCancelEntryComponent(
        Hl7.Fhir.Model.Task task)
    {
        task.Status = Hl7.Fhir.Model.Task.TaskStatus.Cancelled;
        task.BusinessStatus = new CodeableConcept() { Text = "Claimed" };

        return new Bundle.EntryComponent()
        {
            FullUrl = $"http://localhost:8080/pyro/{task.TypeName}/{task.Id}",
            Resource = task,
            Request = new Bundle.RequestComponent()
            {
                Method = Bundle.HTTPVerb.PUT,
                Url = $"{task.TypeName}/{task.Id}",
                IfMatch = $"W/\"{task.Meta.VersionId}\""
            }
        };
    }

    private List<Hl7.Fhir.Model.Task>? ValidateRequestIsClaimable(
        List<Hl7.Fhir.Model.Task> taskList)
    {
        foreach (var task in taskList)
        {
            if (task.Status is null)
            {
                _errorMessageList.Add(
                    "Request's task status was found to be null or empty, all Task resource must have a status defined.");
                return null;
            }

            if (!_claimableTaskStatusList.Contains(task.Status.Value))
            {
                _errorMessageList.Add($"Task {task.Id} status is assigned {task.Status} which can not be claimed");
                return null;
            }
        }

        return taskList;
    }

    private async Task<List<Hl7.Fhir.Model.Task>?> GetRequestTaskResourceList(
        Identifier claimRequestRequisition)
    {
        ArgumentNullException.ThrowIfNull(_fhirNavigator);

        SearchParams fhirQuery =
            GetRequestResourcesListQuery(claimRequestRequisition.System, claimRequestRequisition.Value);

        _fhirNavigator.Cache.Clear();
        SearchInfo searchInfo = await _fhirNavigator.Search<Hl7.Fhir.Model.Task>(fhirQuery, pageLimiter: null);
        if (searchInfo.ResourceTotal == 0)
        {
            return null;
        }

        return _fhirNavigator.Cache.GetList<Hl7.Fhir.Model.Task>();
    }

    // private static SearchParams GetRequestResourcesListQueryOLD(
    //     string claimRequestSystem,
    //     string claimRequestValue)
    // {
    //     SearchParams fhirQuery = new SearchParams();
    //     fhirQuery.Add("group-identifier", $"{claimRequestSystem}|{claimRequestValue}");
    //     fhirQuery.Add("status:not", Hl7.Fhir.Model.Task.TaskStatus.Cancelled.GetLiteral());
    //     fhirQuery.Add(SearchParams.SEARCH_PARAM_INCLUDE, $"{ResourceType.Task.GetLiteral()}:patient");
    //     fhirQuery.Add(SearchParams.SEARCH_PARAM_INCLUDE,
    //         $"{ResourceType.Task.GetLiteral()}:focus:{ResourceType.ServiceRequest.GetLiteral()}");
    //     fhirQuery.Add(SearchParams.SEARCH_PARAM_INCLUDE,
    //         $"{ResourceType.Task.GetLiteral()}:focus:{ResourceType.CommunicationRequest.GetLiteral()}");
    //     fhirQuery.Add(SearchParams.SEARCH_PARAM_INCLUDE,
    //         $"{ResourceType.Task.GetLiteral()}:owner:{ResourceType.Organization.GetLiteral()}");
    //     fhirQuery.Add($"{SearchParams.SEARCH_PARAM_REVINCLUDE}:{IncludeModifier.Iterate.GetLiteral()}",
    //         $"{ResourceType.Consent.GetLiteral()}:data:{ResourceType.ServiceRequest.GetLiteral()}");
    //     //fhirQuery.Add($"{SearchParams.SEARCH_PARAM_INCLUDE}", $"{ResourceType.Task.GetLiteral()}:requester:{ResourceType.PractitionerRole.GetLiteral()}");
    //     fhirQuery.Add($"{SearchParams.SEARCH_PARAM_INCLUDE}:{IncludeModifier.Iterate.GetLiteral()}",
    //         $"{ResourceType.ServiceRequest.GetLiteral()}:requester:{ResourceType.PractitionerRole.GetLiteral()}");
    //     fhirQuery.Add($"{SearchParams.SEARCH_PARAM_INCLUDE}:{IncludeModifier.Iterate.GetLiteral()}",
    //         $"{ResourceType.PractitionerRole.GetLiteral()}:practitioner:{ResourceType.Practitioner.GetLiteral()}");
    //     fhirQuery.Add("_count", "500");
    //
    //     return fhirQuery;
    // }
    
    private static SearchParams GetRequestResourcesListQuery(
        string claimRequestSystem,
        string claimRequestValue)
    {
        SearchParams fhirQuery = new SearchParams();
        fhirQuery.Add("group-identifier", $"{claimRequestSystem}|{claimRequestValue}");
        fhirQuery.Add("status:not", Hl7.Fhir.Model.Task.TaskStatus.Cancelled.GetLiteral());
        return fhirQuery;
    }

    private async Task<Organization?> GetOrganization(
        ResourceReference claimRequestOrganization)
    {
        ArgumentNullException.ThrowIfNull(_fhirNavigator);

        Organization? organization = null;
        if (!string.IsNullOrWhiteSpace(claimRequestOrganization.Reference))
        {
            organization = await _fhirNavigator.GetResource<Organization>(resourceReference: claimRequestOrganization,
                errorLocationDisplay: "ClaimRequest.organization");

            if (organization is null)
            {
                _errorMessageList.Add(
                    $"Unable to locate the {OrganizationParameterName} parameter's Organization resource " +
                    $"from the repository for the Resource reference: {claimRequestOrganization.Reference}");
                return null;
            }

            return organization;
        }

        //Search by Identifier
        var fhirQuery = new SearchParams();
        fhirQuery.Add("identifier",
            $"{claimRequestOrganization.Identifier.System}|{claimRequestOrganization.Identifier.Value}");
        SearchInfo searchInfo = await _fhirNavigator.Search<Organization>(fhirQuery);
        organization = _fhirNavigator.Cache.GetList<Organization>().FirstOrDefault();

        if (organization is null)
        {
            _errorMessageList.Add(
                $"Unable to locate the {OrganizationParameterName} parameter's Organization resource " +
                $"from the repository for the Resource reference's Identifier : " +
                $"{claimRequestOrganization.Identifier.System}|{claimRequestOrganization.Identifier.Value}");
            return null;
        }

        return organization;
    }

    private ClaimOperationOutcome GetErrorClaimOperationOutcome()
    {
        if (_errorMessageList.Count == 0)
        {
            logger.LogError(
                "When construction an error OperationOutcome response, with in the class {ClassName}, zero " +
                "error messages were provided to the method",
                nameof(ClaimOperation));
            
            return new ClaimOperationOutcome(
                Resource: operationOutcomeSupport.GetError(messageList: ["Unknown error"]),
                HttpStatusCode: HttpStatusCode.BadRequest);
        }

        return new ClaimOperationOutcome(
            Resource: operationOutcomeSupport.GetError(messageList: _errorMessageList.ToArray()),
            HttpStatusCode: HttpStatusCode.BadRequest);
    }

    private ClaimOperationOutcome GetSuccessfulClaimOperationOutcome(
        string groupTaskResourceId)
    {
        return new ClaimOperationOutcome(
            Resource: new Parameters()
            {
                Parameter = new List<Parameters.ParameterComponent>()
                {
                    new()
                    {
                        Name = GroupTask,
                        Value = new ResourceReference($"{ResourceType.Task.GetLiteral()}/{groupTaskResourceId}",
                            display: "Group Task")
                    },
                    new()
                    {
                        Name = ResultParameterName,
                        Value = new Coding(
                            system: _requestClaimResultTypeSystemUri.OriginalString,
                            code: "ok")
                    }
                }
            },
            HttpStatusCode: HttpStatusCode.OK);
    }

    private ClaimOperationOutcome GetOrganizationNotFoundClaimOperationOutcome()
    {
        return new ClaimOperationOutcome(
            Resource: new Parameters()
            {
                Parameter = new List<Parameters.ParameterComponent>()
                {
                    new Parameters.ParameterComponent()
                    {
                        Name = ResultParameterName,
                        Value = new Coding(
                            system: _requestClaimResultTypeSystemUri.OriginalString,
                            code: "organization-not-found")
                    }
                }
            },
            HttpStatusCode: HttpStatusCode.OK);
    }

    private ClaimOperationOutcome GetRequisitionNotFoundClaimOperationOutcome()
    {
        return new ClaimOperationOutcome(
            Resource: new Parameters()
            {
                Parameter = new List<Parameters.ParameterComponent>()
                {
                    new Parameters.ParameterComponent()
                    {
                        Name = ResultParameterName,
                        Value = new Coding(
                            system: _requestClaimResultTypeSystemUri.OriginalString,
                            code: "requisition-not-found")
                    }
                }
            },
            HttpStatusCode: HttpStatusCode.OK);
    }

    private ClaimRequest? GetValidClaimRequest(
        Parameters claimParameters)
    {
        if (claimParameters.Parameter.Count == 0)
        {
            _errorMessageList.Add("Zero parameters where found in the Parameters resource");
            return null;
        }

        Identifier? requisitionIdentifier =
            GetParameterFirstOrDefault<Identifier>(parameters: claimParameters,
                parametersName: RequisitionParameterName);
        if (requisitionIdentifier == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(requisitionIdentifier.System) ||
            string.IsNullOrWhiteSpace(requisitionIdentifier.Value))
        {
            _errorMessageList.Add($"The {RequisitionParameterName} parameter must provide an Identifier.system and a " +
                                  $"Identifier.value, one, or both, were not found");
            return null;
        }

        ResourceReference? organizationResourceReference =
            GetParameterFirstOrDefault<ResourceReference>(parameters: claimParameters,
                parametersName: OrganizationParameterName);
        if (organizationResourceReference == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(organizationResourceReference.Reference) &&
            (string.IsNullOrWhiteSpace(organizationResourceReference.Identifier?.System) ||
             string.IsNullOrWhiteSpace(organizationResourceReference.Identifier?.Value)))
        {
            _errorMessageList.Add($"The {OrganizationParameterName} parameter must provide a ResourceReference which " +
                                  $"contains either a ResourceReference.reference or an Identifier with both a system & value");
            return null;
        }

        return new ClaimRequest(requisition: requisitionIdentifier, organization: organizationResourceReference);
    }


    private T? GetParameterFirstOrDefault<T>(
        Parameters parameters,
        string parametersName) where T : Hl7.Fhir.Model.DataType
    {
        var parameterComponentList =
            parameters.Parameter.Where(x => x.Name.Equals(parametersName, StringComparison.OrdinalIgnoreCase));
        Parameters.ParameterComponent? parameterComponent = null;
        foreach (var parameter in parameterComponentList)
        {
            if (parameterComponent is not null)
            {
                _errorMessageList.Add(
                    $"Must provide one, and only one, {parametersName} parameter, found more than one");
                return null;
            }

            parameterComponent = parameter;
        }

        if (parameterComponent is null)
        {
            _errorMessageList.Add($"Must provide one, and only one, {parametersName} parameter");
            return null;
        }

        DataType? fhirDataType = parameterComponent.Value;
        if (fhirDataType is not T targetFhirType)
        {
            _errorMessageList.Add($"The {parametersName} parameter must be of type {nameof(T)}");
            return null;
        }

        return targetFhirType;
    }
}