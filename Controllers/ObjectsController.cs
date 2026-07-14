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

        /// <summary>Synthetic type id used for ISA-95 container objects that have no OPC UA type.</summary>
        private const string ContainerTypePrefix = "ISA95:";

        /// <summary>
        /// Builds the ISA-95 containment tree from the current metadata. The tree synthesizes the
        /// intermediate container levels (Enterprise/Site/Area/Line/Workcell) that are not materialized as
        /// their own rows, and attaches each leaf asset under its deepest populated level.
        /// </summary>
        private Isa95Hierarchy BuildHierarchy() => new(_kusto.GetIsa95LeafAssets());

        /// <summary>Maps an ISA-95 tree node (container/asset or variable) to an I3X object response.</summary>
        private static ObjectInstanceResponse MapNode(Isa95Hierarchy.Node node, bool includeMetadata)
        {
            if (node.Kind == Isa95Hierarchy.NodeKind.Variable)
            {
                // Variable leaf: the value-bearing node. Its type is the OPC UA DataType from the metadata,
                // namespace-qualified so it matches the ids returned by ObjectTypesController and stays unique
                // across namespaces (enabling namespace -> type -> object navigation).
                string typeToken = node.TypeToken;
                string variableType = ObjectTypeId.Build(node.NamespaceUri, typeToken);
                return new ObjectInstanceResponse(
                    node.ElementId,
                    node.DisplayName,
                    variableType,
                    false,
                    node.ParentId,
                    false,
                    includeMetadata ? BuildMetadata(node.NamespaceUri, variableType) : null);
            }

            // Container level (ISA-95 level or the asset/station at the deepest level). These are structural
            // ISA-95 nodes, not OPC UA objects, so they always get an ISA-95 type derived from their level
            // (e.g. "ISA95:Site", "ISA95:Workcell"). Both are compositions.
            string typeId = ContainerTypePrefix + node.Level;

            return new ObjectInstanceResponse(
                node.ElementId,
                node.DisplayName,
                typeId,
                true,
                node.ParentId,
                false,
                includeMetadata ? BuildMetadata(node.IsAsset ? node.NamespaceUri : null, typeId) : null);
        }

        [HttpGet]
        public ActionResult<SuccessResponse<IReadOnlyList<ObjectInstanceResponse>>> GetObjects(
            [FromQuery] string typeElementId = null,
            [FromQuery] bool includeMetadata = false,
            [FromQuery] bool? root = null)
        {
            var hierarchy = BuildHierarchy();

            IEnumerable<Isa95Hierarchy.Node> nodes;

            if (!string.IsNullOrEmpty(typeElementId))
            {
                // A type is a namespace-qualified OPC UA DataType ("<namespaceUri>#<dataType>"), matching the
                // ids returned by ObjectTypesController. Filtering by it returns the variables of that type -
                // this is how the I3X namespace view drills from a namespace's types to their objects.
                var parsed = ObjectTypeId.Parse(typeElementId);
                nodes = hierarchy.ById.Values.Where(n =>
                    n.Kind == Isa95Hierarchy.NodeKind.Variable &&
                    (parsed is { } p
                        ? string.Equals(n.NamespaceUri, p.NamespaceUri, StringComparison.Ordinal) &&
                          string.Equals(n.TypeToken, p.Name, StringComparison.Ordinal)
                        : string.Equals(n.TypeToken, typeElementId, StringComparison.Ordinal)));
            }
            else if (root == true)
            {
                // Roots are the top-most ISA-95 container levels (or root assets when a server provides no
                // ISA-95 context at all).
                nodes = hierarchy.Roots;
            }
            else
            {
                nodes = hierarchy.ById.Values;
            }

            var results = nodes.Select(n => MapNode(n, includeMetadata)).ToList();

            return Ok(new SuccessResponse<IReadOnlyList<ObjectInstanceResponse>>(true, results));
        }

        [HttpPost("list")]
        public ActionResult<BulkResponse<ObjectInstanceResponse>> ListObjects([FromBody] GetObjectsRequest request)
        {
            var hierarchy = BuildHierarchy();

            var items = request.ElementIds.Select(id => hierarchy.TryGet(id, out var node)
                ? BulkResultItem<ObjectInstanceResponse>.Ok(id, MapNode(node, request.IncludeMetadata))
                : BulkResultItem<ObjectInstanceResponse>.NotFound(id, "Object not found")).ToList();

            return Ok(new BulkResponse<ObjectInstanceResponse>(true, items));
        }

        [HttpPost("related")]
        public ActionResult<BulkResponse<List<RelatedObjectResult>>> QueryRelatedObjects([FromBody] GetRelatedObjectsRequest request)
        {
            // Resolve the requested relationship type (default HasComponent). i3X relationships are
            // directional: a "forward" type (e.g. HasComponent, Organizes) walks from a parent to its
            // children, and its reverse (e.g. ComponentOf, OrganizedBy) walks from a child to its parent.
            string requestedType = string.IsNullOrEmpty(request.RelationshipType) ? "HasComponent" : request.RelationshipType;
            RelationshipDirection direction = ResolveDirection(requestedType);

            var hierarchy = BuildHierarchy();

            var items = request.ElementIds.Select(id =>
            {
                if (!hierarchy.TryGet(id, out _))
                {
                    return BulkResultItem<List<RelatedObjectResult>>.NotFound(id, "Object not found");
                }

                // Navigate the synthesized ISA-95 tree: forward => direct children, reverse => parent.
                // Unsupported/unknown relationship types yield no related objects (rather than mislabeling).
                IEnumerable<Isa95Hierarchy.Node> neighbors = direction switch
                {
                    RelationshipDirection.Forward => hierarchy.ChildrenOf(id),
                    RelationshipDirection.Reverse => hierarchy.ParentOf(id) is { } parent
                        ? new[] { parent }
                        : Array.Empty<Isa95Hierarchy.Node>(),
                    _ => Array.Empty<Isa95Hierarchy.Node>()
                };

                var related = neighbors
                    .Select(n => new RelatedObjectResult(requestedType, MapNode(n, request.IncludeMetadata)))
                    .ToList();

                return BulkResultItem<List<RelatedObjectResult>>.Ok(id, related);
            }).ToList();

            return Ok(new BulkResponse<List<RelatedObjectResult>>(true, items));
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
            var hierarchy = BuildHierarchy();

            // Map each requested ElementId to the underlying telemetry Subject(s) so both asset ids and
            // individual variable ids (Subject::Name) can be answered.
            var subjects = ResolveSubjects(hierarchy, request.ElementIds);
            string inClause = ADXDataService.ToKqlStringList(subjects.ToArray());

            string query = "opcua_telemetry\r\n"
                         + "| where Subject in (" + inClause + ")\r\n"
                         + "| where Timestamp > now(-1h)\r\n"
                         + "| summarize arg_max(Timestamp, Value) by Subject, Name\r\n"
                         + "| project Subject, Name, Timestamp, Value = tostring(Value)";

            var rows = subjects.Count == 0 ? new List<Dictionary<string, object>>() : _kusto.RunQueryRows(query);

            // Index telemetry by Subject, and by Subject::Name for direct variable lookups.
            var bySubject = rows.GroupBy(r => Str(r, "Subject"))
                                .ToDictionary(g => g.Key, g => g.ToList());

            var items = request.ElementIds.Select(id =>
            {
                if (!hierarchy.TryGet(id, out var node))
                {
                    return BulkResultItem<CurrentValueResult>.NotFound(id, "Object not found");
                }

                // Variable leaf: return the single scalar value of this field.
                if (node.Kind == Isa95Hierarchy.NodeKind.Variable)
                {
                    var row = bySubject.TryGetValue(node.Subject, out var g)
                        ? g.FirstOrDefault(r => Str(r, "Name") == node.VariableName)
                        : null;
                    if (row == null)
                    {
                        return BulkResultItem<CurrentValueResult>.NotFound(id, "No current value available");
                    }

                    string ts = row.TryGetValue("Timestamp", out var t) && t is DateTime dt ? ToRfc3339(dt) : "";
                    var vr = new CurrentValueResult(false, row.GetValueOrDefault("Value"), "Good", ts);
                    return BulkResultItem<CurrentValueResult>.Ok(id, vr);
                }

                // Asset (or other composition): return the components map of all its variables.
                if (node.IsAsset && bySubject.TryGetValue(node.Subject, out var group))
                {
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
                }

                // Containers (and assets with no telemetry) have no direct current value.
                return BulkResultItem<CurrentValueResult>.NotFound(id, "No current value available");
            }).ToList();

            return Ok(new BulkResponse<CurrentValueResult>(true, items));
        }

        [HttpPost("history")]
        public ActionResult<BulkResponse<HistoricalValueResult>> QueryHistory([FromBody] GetObjectHistoryRequest request)
        {
            var hierarchy = BuildHierarchy();
            var subjects = ResolveSubjects(hierarchy, request.ElementIds);
            string inClause = ADXDataService.ToKqlStringList(subjects.ToArray());
            string start = request.StartTime ?? DateTime.UtcNow.AddHours(-1).ToString("o");
            string end = request.EndTime ?? DateTime.UtcNow.ToString("o");

            string query = "opcua_telemetry\r\n"
                         + "| where Subject in (" + inClause + ")\r\n"
                         + "| where Timestamp between (datetime(\"" + start + "\") .. datetime(\"" + end + "\"))\r\n"
                         + "| project Subject, Name, Timestamp, Value = tostring(Value)\r\n"
                         + "| sort by Subject asc, Timestamp desc";

            var rows = subjects.Count == 0 ? new List<Dictionary<string, object>>() : _kusto.RunQueryRows(query);

            var bySubject = rows.GroupBy(r => Str(r, "Subject"))
                                .ToDictionary(g => g.Key, g => g.ToList());

            var items = request.ElementIds.Select(id =>
            {
                if (!hierarchy.TryGet(id, out var node))
                {
                    return BulkResultItem<HistoricalValueResult>.NotFound(id, "Object not found");
                }

                // Variable leaf: return its own time series directly in "values".
                if (node.Kind == Isa95Hierarchy.NodeKind.Variable)
                {
                    var series = bySubject.TryGetValue(node.Subject, out var g)
                        ? g.Where(r => Str(r, "Name") == node.VariableName)
                        : Enumerable.Empty<Dictionary<string, object>>();

                    var values = series
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

                    return BulkResultItem<HistoricalValueResult>.Ok(id,
                        new HistoricalValueResult(false, values, null));
                }

                // Asset: return per-variable series in the components map.
                if (node.IsAsset && bySubject.TryGetValue(node.Subject, out var group))
                {
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
                }

                // Containers (and assets with no telemetry) have no historical values.
                return BulkResultItem<HistoricalValueResult>.NotFound(id, "No historical values available");
            }).ToList();

            return Ok(new BulkResponse<HistoricalValueResult>(true, items));
        }

        /// <summary>
        /// Resolves the requested ElementIds (which may be container, asset or variable ids) to the distinct
        /// set of underlying telemetry Subjects to query.
        /// </summary>
        private static IReadOnlyCollection<string> ResolveSubjects(Isa95Hierarchy hierarchy, IEnumerable<string> elementIds)
        {
            var subjects = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in elementIds)
            {
                if (hierarchy.TryGet(id, out var node) && !string.IsNullOrEmpty(node.Subject))
                {
                    subjects.Add(node.Subject);
                }
            }
            return subjects;
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