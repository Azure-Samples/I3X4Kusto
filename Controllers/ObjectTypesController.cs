using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;

namespace I3X4Kusto.Controllers
{
    [ApiController]
    [Route("v1/objecttypes")]
    public sealed class ObjectTypesController : ControllerBase
    {
        private readonly ADXDataService _kusto;

        public ObjectTypesController(ADXDataService kusto)
        {
            _kusto = kusto;
            _kusto.Connect();
        }

        [HttpGet]
        public ActionResult<SuccessResponse<IReadOnlyList<ObjectTypeResponse>>> GetObjectTypes(
            [FromQuery] string namespaceUri = null)
        {
            string query = ADXDataService.NamespaceBySubjectPrelude
                         + "opcua_metadata_lkv\r\n"
                         + ADXDataService.ResolveNamespaceUri() + "\r\n";
            if (!string.IsNullOrEmpty(namespaceUri))
            {
                query += "| where NamespaceUri in (" + ADXDataService.ToKqlStringList([namespaceUri]) + ")\r\n";
            }
            query += "| distinct Type, NamespaceUri\r\n"
                   + "| project Type, NamespaceUri";

            var rows = _kusto.RunQueryRows(query);

            var results = rows.Select(MapObjectType).ToList();

            return Ok(new SuccessResponse<IReadOnlyList<ObjectTypeResponse>>(true, results));
        }

        [HttpPost("query")]
        public ActionResult<BulkResponse<ObjectTypeResponse>> QueryByElementId([FromBody] GetObjectTypesRequest request)
        {
            string inClause = ADXDataService.ToKqlStringList(request.ElementIds);

            string kql = ADXDataService.NamespaceBySubjectPrelude
                       + "opcua_metadata_lkv\r\n"
                       + "| where Type in (" + inClause + ")\r\n"
                       + ADXDataService.ResolveNamespaceUri() + "\r\n"
                       + "| distinct Type, NamespaceUri\r\n"
                       + "| project Type, NamespaceUri";

            var rows = _kusto.RunQueryRows(kql);

            var byId = rows
                .GroupBy(r => Str(r, "Type"))
                .ToDictionary(g => g.Key, g => MapObjectType(g.First()));

            var items = request.ElementIds.Select(id => byId.TryGetValue(id, out var ot)
                ? BulkResultItem<ObjectTypeResponse>.Ok(id, ot)
                : BulkResultItem<ObjectTypeResponse>.NotFound(id, "Object type not found")).ToList();

            return Ok(new BulkResponse<ObjectTypeResponse>(true, items));
        }

        private static ObjectTypeResponse MapObjectType(Dictionary<string, object> r)
        {
            var type = Str(r, "Type");
            return new ObjectTypeResponse(
                type,
                type,
                Str(r, "NamespaceUri"),
                type,
                new Dictionary<string, object>());
        }

        private static string Str(Dictionary<string, object> row, string key) =>
            row.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
    }
}
