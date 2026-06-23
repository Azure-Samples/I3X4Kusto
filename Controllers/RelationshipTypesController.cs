using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;

namespace I3X4Kusto.Controllers
{
    [ApiController]
    [Route("v1/relationshiptypes")]
    public sealed class RelationshipTypesController : ControllerBase
    {
        private readonly ADXDataService _kusto;

        // Well-known OPC UA reference types exposed through this adapter
        private static readonly List<RelationshipType> KnownRelationshipTypes =
        [
            new("HasComponent", "HasComponent", "http://opcfoundation.org/UA/", "HasComponent", "ComponentOf"),
            new("Organizes", "Organizes", "http://opcfoundation.org/UA/", "Organizes", "OrganizedBy"),
            new("HasProperty", "HasProperty", "http://opcfoundation.org/UA/", "HasProperty", "PropertyOf"),
            new("HasSubtype", "HasSubtype", "http://opcfoundation.org/UA/", "HasSubtype", "SubtypeOf"),
            new("HasTypeDefinition", "HasTypeDefinition", "http://opcfoundation.org/UA/", "HasTypeDefinition", "TypeDefinitionOf"),
            new("HasModellingRule", "HasModellingRule", "http://opcfoundation.org/UA/", "HasModellingRule", "ModellingRuleOf"),
            new("HasEncoding", "HasEncoding", "http://opcfoundation.org/UA/", "HasEncoding", "EncodingOf"),
            new("HasDescription", "HasDescription", "http://opcfoundation.org/UA/", "HasDescription", "DescriptionOf"),
            new("GeneratesEvent", "GeneratesEvent", "http://opcfoundation.org/UA/", "GeneratesEvent", "GeneratedBy"),
            new("AlwaysGeneratesEvent", "AlwaysGeneratesEvent", "http://opcfoundation.org/UA/", "AlwaysGeneratesEvent", "AlwaysGeneratedBy"),
            new("HasNotifier", "HasNotifier", "http://opcfoundation.org/UA/", "HasNotifier", "NotifierOf"),
            new("HasEventSource", "HasEventSource", "http://opcfoundation.org/UA/", "HasEventSource", "EventSourceOf"),
            new("HasCondition", "HasCondition", "http://opcfoundation.org/UA/", "HasCondition", "IsConditionOf"),
            new("HasOrderedComponent", "HasOrderedComponent", "http://opcfoundation.org/UA/", "HasOrderedComponent", "OrderedComponentOf"),
            new("FromState", "FromState", "http://opcfoundation.org/UA/", "FromState", "ToTransition"),
            new("ToState", "ToState", "http://opcfoundation.org/UA/", "ToState", "FromTransition"),
            new("HasCause", "HasCause", "http://opcfoundation.org/UA/", "HasCause", "MayBeCausedBy"),
            new("HasEffect", "HasEffect", "http://opcfoundation.org/UA/", "HasEffect", "MayBeAffectedBy"),
            new("HasGuard", "HasGuard", "http://opcfoundation.org/UA/", "HasGuard", "GuardOf")
        ];

        public RelationshipTypesController(ADXDataService kusto)
        {
            _kusto = kusto;
            _kusto.Connect();
        }

        [HttpGet]
        public ActionResult<SuccessResponse<IReadOnlyList<RelationshipType>>> GetRelationshipTypes(
            [FromQuery] string namespaceUri = null)
        {
            // No explicit relationship table in ADX – return well-known OPC UA reference types
            IReadOnlyList<RelationshipType> results = string.IsNullOrEmpty(namespaceUri)
                ? KnownRelationshipTypes
                : KnownRelationshipTypes.Where(rt => rt.NamespaceUri == namespaceUri).ToList();

            return Ok(new SuccessResponse<IReadOnlyList<RelationshipType>>(true, results));
        }

        [HttpPost("query")]
        public ActionResult<BulkResponse<RelationshipType>> QueryByElementId([FromBody] GetRelationshipTypesRequest request)
        {
            var byId = KnownRelationshipTypes.ToDictionary(rt => rt.ElementId);

            var items = request.ElementIds.Select(id => byId.TryGetValue(id, out var rt)
                ? BulkResultItem<RelationshipType>.Ok(id, rt)
                : BulkResultItem<RelationshipType>.NotFound(id, "Relationship type not found")).ToList();

            return Ok(new BulkResponse<RelationshipType>(true, items));
        }
    }
}
