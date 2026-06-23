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
            query += "| project NodeId, DisplayName, Type, DataSetWriterID, NamespaceUri";

            var rows = _kusto.RunQueryRows(query);

            var results = rows.Select(r => MapObject(r, includeMetadata)).ToList();

            return Ok(new SuccessResponse<IReadOnlyList<ObjectInstanceResponse>>(true, results));
        }

        [HttpPost("list")]
        public ActionResult<BulkResponse<ObjectInstanceResponse>> ListObjects([FromBody] GetObjectsRequest request)
        {
            string inClause = ADXDataService.ToKqlStringList(request.ElementIds);

            string query = "opcua_metadata_lkv\r\n"
                         + "| where DataSetWriterID in (" + inClause + ")\r\n"
                         + "| project NodeId, DisplayName, Type, DataSetWriterID, NamespaceUri";

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
            string sourceRelationship = string.IsNullOrEmpty(request.RelationshipType) ? "HasComponent" : request.RelationshipType;

            // Determine which requested Objects (by DataSetWriterID) actually exist,
            // so unknown elementIds can be reported as NotFound.
            string existsQuery = "opcua_metadata_lkv\r\n"
                               + "| where DataSetWriterID in (" + inClause + ")\r\n"
                               + "| distinct DataSetWriterID";

            var existing = new HashSet<string>(
                _kusto.RunQueryRows(existsQuery).Select(r => Str(r, "DataSetWriterID")));

            // Related Objects are siblings: other Objects that share the same parent (NodeId).
            string query = "opcua_metadata_lkv\r\n"
                         + "| where DataSetWriterID in (" + inClause + ")\r\n"
                         + "| where isnotempty(NodeId)\r\n"
                         + "| distinct SourceId = DataSetWriterID, NodeId\r\n"
                         + "| join kind=inner (\r\n"
                         + "    opcua_metadata_lkv\r\n"
                         + "    | distinct DataSetWriterID, NodeId, DisplayName, Type, NamespaceUri\r\n"
                         + ") on NodeId\r\n"
                         + "| where DataSetWriterID != SourceId\r\n"
                         + "| project SourceId, NodeId, DisplayName, Type, DataSetWriterID, NamespaceUri";

            var rows = _kusto.RunQueryRows(query);

            var bySource = rows
                .GroupBy(r => Str(r, "SourceId"))
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(r => new RelatedObjectResult(
                        sourceRelationship,
                        MapObject(r, request.IncludeMetadata))).ToList());

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

        [HttpPost("value")]
        public ActionResult<BulkResponse<CurrentValueResult>> QueryValue([FromBody] GetObjectValueRequest request)
        {
            string inClause = ADXDataService.ToKqlStringList(request.ElementIds);

            string query = "opcua_telemetry\r\n"
                         + "| where DataSetWriterID in (" + inClause + ")\r\n"
                         + "| where Timestamp > now(- 1h)\r\n"
                         + "| summarize arg_max(Timestamp, Value) by DataSetWriterID, Name\r\n"
                         + "| project DataSetWriterID, Name, Timestamp, Value = todouble(Value)\r\n"
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
                         + "| where DataSetWriterID in (" + inClause + ")\r\n"
                         + "| where Timestamp between (datetime(\"" + start + "\") .. datetime(\"" + end + "\"))\r\n"
                         + "| project DataSetWriterID, Name, Timestamp, Value = todouble(Value)\r\n"
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