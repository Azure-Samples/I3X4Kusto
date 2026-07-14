using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;

namespace I3X4Kusto.Controllers
{
    [ApiController]
    [Route("v1/namespaces")]
    public sealed class NamespacesController : ControllerBase
    {
        private readonly ADXDataService _kusto;

        public NamespacesController(ADXDataService kusto)
        {
            _kusto = kusto;
            _kusto.Connect();
        }

        [HttpGet]
        public ActionResult<SuccessResponse<IReadOnlyList<Namespace>>> GetNamespaces()
        {
            string query = ADXDataService.NamespaceBySubjectPrelude
                         + "NamespaceBySubject\r\n"
                         + "| where isnotempty(NamespaceUri)\r\n"
                         + "| distinct NamespaceUri";

            var rows = _kusto.RunQueryRows(query);

            var results = rows
                .Select(r => Str(r, "NamespaceUri"))
                .Where(uri => !string.IsNullOrEmpty(uri))
                .Distinct()
                .OrderBy(uri => uri, System.StringComparer.Ordinal)
                .Select(uri => new Namespace(uri, ExtractNameFromUri(uri)))
                .ToList();

            return Ok(new SuccessResponse<IReadOnlyList<Namespace>>(true, results));
        }

        private static string ExtractNameFromUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return "";
            var trimmed = uri.TrimEnd('/');
            int lastSlash = trimmed.LastIndexOf('/');
            return lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        }

        private static string Str(Dictionary<string, object> row, string key) =>
            row.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
    }
}
