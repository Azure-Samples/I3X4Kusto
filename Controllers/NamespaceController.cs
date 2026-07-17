using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

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
                .SelectMany(ExpandNamespaceUris)
                .Where(uri => !string.IsNullOrEmpty(uri))
                .Distinct()
                .OrderBy(uri => uri, System.StringComparer.Ordinal)
                .Select(uri => new Namespace(uri, ExtractNameFromUri(uri)))
                .ToList();

            // Always advertise the synthetic ISA-95 namespace that the container object types belong to,
            // so every Object Type resolves to a declared Namespace.
            if (!results.Any(n => n.Uri == Isa95Hierarchy.Isa95NamespaceUri))
            {
                results.Add(new Namespace(Isa95Hierarchy.Isa95NamespaceUri, "ISA95"));
            }

            return Ok(new SuccessResponse<IReadOnlyList<Namespace>>(true, results));
        }

        // A Subject's NamespaceUri may be a single URI or, for multi-namespace assets, a JSON array of URIs
        // (e.g. ["http://a/","http://b/"]) carried through as a string. Flatten either form into individual
        // URIs so each namespace is surfaced separately instead of as one malformed entry.
        private static IEnumerable<string> ExpandNamespaceUris(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                yield break;
            }

            string trimmed = raw.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                string[] parsed = null;
                try
                {
                    parsed = JsonSerializer.Deserialize<string[]>(trimmed);
                }
                catch (JsonException)
                {
                    // Not valid JSON after all; fall through and return the raw value.
                }

                if (parsed != null)
                {
                    foreach (var uri in parsed)
                    {
                        if (!string.IsNullOrEmpty(uri))
                        {
                            yield return uri;
                        }
                    }

                    yield break;
                }
            }

            yield return raw;
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
