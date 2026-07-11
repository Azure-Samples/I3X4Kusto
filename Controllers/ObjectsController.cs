using I3X4Kusto;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;

namespace I3xKustoAdapter.Controllers
{
    [ApiController]
    [Route("v1/objects")]
    public sealed class ObjectsController : ControllerBase
    {
        private readonly ADXDataService _kusto;

        public ObjectsController(ADXDataService kusto)
        {
            _kusto = kusto;
            _kusto.Connect();
        }

        [HttpGet]
        public ActionResult<SuccessResponse<IReadOnlyList<ObjectInstanceResponse>>> GetObjects(
            [FromQuery] string typeElementId = null,
            [FromQuery] bool includeMetadata = false,
            [FromQuery] bool? root = null)
        {
            string query = "opcua_metadata_lkv\r\n";
            if (!string.IsNullOrEmpty(typeElementId))
            {
                // Objects whose telemetry includes the given variable type
                query += "| where Type in (" + ADXDataService.ToKqlStringList([typeElementId]) + ")\r\n";
            }
            if (root == true)
            {
                // Root Objects are those without a parent node.
                query += "| where isempty(NodeId)\r\n";
            }
            query += "| project NodeId, DisplayName, Type, DataSetWriterID = Subject, NamespaceUri";

            var rows = _kusto.RunQueryRows(query);

            var results = rows.Select(r => MapObject(r, includeMetadata)).ToList();

            return Ok(new SuccessResponse<IReadOnlyList<ObjectInstanceResponse>>(true, results));
        }

        [HttpPost("list")]
        public ActionResult<BulkResponse<ObjectInstanceResponse>> ListObjects([FromBody] GetObjectsRequest request)
        {
            string inClause = ADXDataService.ToKqlStringList(request.ElementIds);

            string query = "opcua_metadata_lkv\r\n"
                         + "| where Subject in (" + inClause + ")\r\n"
                         + "| project NodeId, DisplayName, Type, DataSetWriterID = Subject, NamespaceUri";

            var rows = _kusto.RunQueryRows(query);

            var byId = rows
                .GroupBy(r => Str(r, "DataSetWriterID"))
                .ToDictionary(g => g.Key, g => MapObject(g.First(), request.IncludeMetadata));

            var items = request.ElementIds.Select(id => byId.TryGetValue(id, out var obj)
                ? BulkResultItem<ObjectInstanceResponse>.Ok(id, obj)
                : BulkResultItem<ObjectInstanceResponse>.NotFound(id, "Object not found")).ToList();

            return Ok(new BulkResponse<ObjectInstanceResponse>(true, items));
        }

        [HttpPost("related")]
        public ActionResult<BulkResponse<List<RelatedObjectResult>>> QueryRelatedObjects([FromBody] GetRelatedObjectsRequest request)
        {
            string inClause = ADXDataService.ToKqlStringList(request.ElementIds);

            // Resolve the requested relationship type (default HasComponent). i3X relationships are
            // directional: a "forward" type (e.g. HasComponent, Organizes) walks from a parent to its
            // children, and its reverse (e.g. ComponentOf, OrganizedBy) walks from a child to its parent.
            string requestedType = string.IsNullOrEmpty(request.RelationshipType) ? "HasComponent" : request.RelationshipType;
            RelationshipDirection direction = ResolveDirection(requestedType);

            // Determine which requested Objects (by DataSetWriterID) actually exist,
            // so unknown elementIds can be reported as NotFound.
            string existsQuery = "opcua_metadata_lkv\r\n"
                               + "| where Subject in (" + inClause + ")\r\n"
                               + "| distinct DataSetWriterID = Subject";

            var existing = new HashSet<string>(
                _kusto.RunQueryRows(existsQuery).Select(r => Str(r, "DataSetWriterID")));

            // An unknown/unsupported relationship type yields no related objects (rather than mislabeling).
            var bySource = direction == RelationshipDirection.Unsupported
                ? new Dictionary<string, List<RelatedObjectResult>>()
                : QueryRelatedByIsa95(inClause, requestedType, direction, request.IncludeMetadata);

            var items = request.ElementIds.Select(id =>
            {
                if (!existing.Contains(id))
                {
                    return BulkResultItem<List<RelatedObjectResult>>.NotFound(id, "Object not found");
                }

                var related = bySource.TryGetValue(id, out var rel) ? rel : new List<RelatedObjectResult>();
                return BulkResultItem<List<RelatedObjectResult>>.Ok(id, related);
            }).ToList();

            return Ok(new BulkResponse<List<RelatedObjectResult>>(true, items));
        }

        /// <summary>
        /// Walks the ISA-95 containment hierarchy (Enterprise &gt; Site &gt; Area &gt; Line &gt; Workcell)
        /// carried in the OPC UA metadata. Forward (HasComponent/Organizes) returns the direct children
        /// of each source object; reverse (ComponentOf/OrganizedBy) returns the direct parent. Falls back
        /// to NodeId-sibling grouping when the ISA-95 levels are not populated.
        /// </summary>
        private Dictionary<string, List<RelatedObjectResult>> QueryRelatedByIsa95(
            string inClause, string sourceRelationship, RelationshipDirection direction, bool includeMetadata)
        {
            // Bring back the source objects with their full ISA-95 path, then all candidate objects, and
            // relate them by direct containment (parent/child differ by exactly one populated level).
            string query = "let sources = opcua_metadata_lkv\r\n"
                         + "| where Subject in (" + inClause + ")\r\n"
                         + "| distinct SourceId = Subject, Workcell, Line, Area, Site, Enterprise, NodeId;\r\n"
                         + "let candidates = opcua_metadata_lkv\r\n"
                         + "| distinct Subject, NodeId, DisplayName, Type, NamespaceUri, Workcell, Line, Area, Site, Enterprise;\r\n"
                         + "sources\r\n"
                         + "| extend srcDepth = tolong(isnotempty(Enterprise)) + tolong(isnotempty(Site)) + tolong(isnotempty(Area)) + tolong(isnotempty(Line)) + tolong(isnotempty(Workcell))\r\n"
                         + "| join kind=inner (\r\n"
                         + "    candidates\r\n"
                         + "    | extend candDepth = tolong(isnotempty(Enterprise)) + tolong(isnotempty(Site)) + tolong(isnotempty(Area)) + tolong(isnotempty(Line)) + tolong(isnotempty(Workcell))\r\n"
                         + ") on Enterprise\r\n"
                         + "| where Subject != SourceId\r\n"
                         // Same higher-level ancestry, differing by exactly one containment level.
                         + "| where (Site == Site1) and (Area == Area1) and (Line == Line1)\r\n";

            // Direction selects whether we return the deeper (child) or shallower (parent) neighbor.
            query += direction == RelationshipDirection.Reverse
                ? "| where candDepth == srcDepth - 1\r\n"
                : "| where candDepth == srcDepth + 1\r\n";

            query += "| project SourceId, DataSetWriterID = Subject, NodeId = NodeId1, DisplayName = DisplayName1, Type = Type1, NamespaceUri = NamespaceUri1";

            List<Dictionary<string, object>> rows;
            try
            {
                rows = _kusto.RunQueryRows(query);
            }
            catch
            {
                rows = new List<Dictionary<string, object>>();
            }

            // Fallback: if ISA-95 levels are not populated, group by shared parent NodeId (siblings).
            if (rows.Count == 0)
            {
                rows = QueryRelatedBySiblingNodeId(inClause);
            }

            return rows
                .GroupBy(r => Str(r, "SourceId"))
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(r => new RelatedObjectResult(
                        sourceRelationship,
                        MapObject(r, includeMetadata))).ToList());
        }

        /// <summary>Legacy behavior: related objects are siblings sharing the same parent NodeId.</summary>
        private List<Dictionary<string, object>> QueryRelatedBySiblingNodeId(string inClause)
        {
            string query = "opcua_metadata_lkv\r\n"
                         + "| where Subject in (" + inClause + ")\r\n"
                         + "| where isnotempty(NodeId)\r\n"
                         + "| distinct SourceId = Subject, NodeId\r\n"
                         + "| join kind=inner (\r\n"
                         + "    opcua_metadata_lkv\r\n"
                         + "    | distinct Subject, NodeId, DisplayName, Type, NamespaceUri\r\n"
                         + ") on NodeId\r\n"
                         + "| where Subject != SourceId\r\n"
                         + "| project SourceId, NodeId, DisplayName, Type, DataSetWriterID = Subject, NamespaceUri";

            return _kusto.RunQueryRows(query);
        }

        /// <summary>Direction of an i3X relationship relative to the ISA-95 containment hierarchy.</summary>
        private enum RelationshipDirection
        {
            Forward,     // parent -> children (HasComponent, Organizes, ...)
            Reverse,     // child -> parent   (ComponentOf, OrganizedBy, ...)
            Unsupported
        }

        // Forward containment relationship types and their reverses, matching RelationshipTypesController.
        private static readonly HashSet<string> ForwardContainmentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "HasComponent", "HasOrderedComponent", "Organizes", "HasProperty"
        };

        private static readonly HashSet<string> ReverseContainmentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "ComponentOf", "OrderedComponentOf", "OrganizedBy", "PropertyOf"
        };

        private static RelationshipDirection ResolveDirection(string relationshipType)
        {
            if (ForwardContainmentTypes.Contains(relationshipType))
            {
                return RelationshipDirection.Forward;
            }

            if (ReverseContainmentTypes.Contains(relationshipType))
            {
                return RelationshipDirection.Reverse;
            }

            return RelationshipDirection.Unsupported;
        }

        [HttpPost("value")]
        public ActionResult<BulkResponse<CurrentValueResult>> QueryValue([FromBody] GetObjectValueRequest request)
        {
            string inClause = ADXDataService.ToKqlStringList(request.ElementIds);

            string query = "opcua_telemetry\r\n"
                         + "| where Subject in (" + inClause + ")\r\n"
                         + "| where Timestamp > now(- 1h)\r\n"
                         + "| summarize arg_max(Timestamp, Value) by Subject, Name\r\n"
                         + "| project DataSetWriterID = Subject, Name, Timestamp, Value = todouble(Value)\r\n"
                         + "| sort by DataSetWriterID asc, Timestamp desc";

            var rows = _kusto.RunQueryRows(query);

            var byId = rows.GroupBy(r => Str(r, "DataSetWriterID"))
                           .ToDictionary(g => g.Key, g => g.ToList());

            var items = request.ElementIds.Select(id =>
            {
                if (!byId.TryGetValue(id, out var group))
                {
                    return BulkResultItem<CurrentValueResult>.NotFound(id, "No current value available");
                }

                var components = new Dictionary<string, VQT>();
                DateTime latest = DateTime.MinValue;

                foreach (var row in group)
                {
                    string ts = "";
                    if (row.TryGetValue("Timestamp", out var t) && t is DateTime dt)
                    {
                        if (dt > latest) latest = dt;
                        ts = ToRfc3339(dt);
                    }

                    components[Str(row, "Name")] = new VQT(row.GetValueOrDefault("Value"), "Good", ts);
                }

                var result = new CurrentValueResult(
                    true,
                    null,
                    "Good",
                    latest == DateTime.MinValue ? "" : ToRfc3339(latest),
                    request.MaxDepth != 1 ? components : null);

                return BulkResultItem<CurrentValueResult>.Ok(id, result);
            }).ToList();

            return Ok(new BulkResponse<CurrentValueResult>(true, items));
        }

        [HttpPost("history")]
        public ActionResult<BulkResponse<HistoricalValueResult>> QueryHistory([FromBody] GetObjectHistoryRequest request)
        {
            string inClause = ADXDataService.ToKqlStringList(request.ElementIds);
            string start = request.StartTime ?? DateTime.UtcNow.AddHours(-1).ToString("o");
            string end = request.EndTime ?? DateTime.UtcNow.ToString("o");

            string query = "opcua_telemetry\r\n"
                         + "| where Subject in (" + inClause + ")\r\n"
                         + "| where Timestamp between (datetime(\"" + start + "\") .. datetime(\"" + end + "\"))\r\n"
                         + "| project DataSetWriterID = Subject, Name, Timestamp, Value = todouble(Value)\r\n"
                         + "| sort by DataSetWriterID asc, Timestamp desc";

            var rows = _kusto.RunQueryRows(query);

            var byId = rows.GroupBy(r => Str(r, "DataSetWriterID"))
                           .ToDictionary(g => g.Key, g => g.ToList());

            var items = request.ElementIds.Select(id =>
            {
                if (!byId.TryGetValue(id, out var group))
                {
                    return BulkResultItem<HistoricalValueResult>.NotFound(id, "No historical values available");
                }

                var components = new Dictionary<string, object>();
                foreach (var nameGroup in group.GroupBy(r => Str(r, "Name")))
                {
                    var vqts = nameGroup
                        .Select(r => new
                        {
                            Row = r,
                            Ts = r.TryGetValue("Timestamp", out var t) && t is DateTime dt ? dt : DateTime.MinValue
                        })
                        .OrderByDescending(x => x.Ts)
                        .Select(x => new VQT(
                            x.Row.GetValueOrDefault("Value"),
                            "Good",
                            x.Ts == DateTime.MinValue ? "" : ToRfc3339(x.Ts)))
                        .ToList();

                    components[nameGroup.Key] = vqts;
                }

                var result = new HistoricalValueResult(
                    true,
                    Array.Empty<VQT>(),
                    request.MaxDepth != 1 ? components : null);

                return BulkResultItem<HistoricalValueResult>.Ok(id, result);
            }).ToList();

            return Ok(new BulkResponse<HistoricalValueResult>(true, items));
        }

        private static ObjectInstanceResponse MapObject(Dictionary<string, object> r, bool includeMetadata)
        {
            string parentId = Str(r, "NodeId");
            return new ObjectInstanceResponse(
                Str(r, "DataSetWriterID"),
                Str(r, "DisplayName"),
                Str(r, "Type"),
                false,
                string.IsNullOrEmpty(parentId) ? null : parentId,
                false,
                includeMetadata ? BuildMetadata(Str(r, "NamespaceUri"), Str(r, "Type")) : null);
        }

        private static ObjectInstanceMetadata BuildMetadata(string namespaceUri, string sourceTypeId) =>
            new(TypeNamespaceUri: string.IsNullOrEmpty(namespaceUri) ? null : namespaceUri,
                SourceTypeId: string.IsNullOrEmpty(sourceTypeId) ? null : sourceTypeId);

        private static string ToRfc3339(DateTime dt) =>
            new DateTimeOffset(dt, TimeSpan.Zero).ToString("o");

        private static string Str(Dictionary<string, object> row, string key) =>
            row.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
    }
}